import os
import sys

# Ensure UTF-8 output on Windows consoles
try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import zipfile
import tempfile
import time
import requests

def log(msg):
    try:
        print(f"[MSSTORE] {msg}", flush=True)
    except Exception:
        clean = msg.encode("ascii", "ignore").decode("ascii")
        print(f"[MSSTORE] {clean}", flush=True)

def publish(pkg_path, tenant_id, client_id, client_secret, app_id, seller_id=None):
    if not os.path.exists(pkg_path):
        log(f"[ERROR] Package not found: {pkg_path}")
        sys.exit(1)

    log(f"[INFO] Found package: {pkg_path} ({os.path.getsize(pkg_path) / (1024*1024):.2f} MB)")
    
    # 1. Obtain Azure AD OAuth2 Token
    log("[AUTH] Authenticating with Microsoft Dev Center (Azure AD)...")
    token_url = f"https://login.microsoftonline.com/{tenant_id}/oauth2/token"
    token_data = {
        "grant_type": "client_credentials",
        "client_id": client_id,
        "client_secret": client_secret,
        "resource": "https://manage.devcenter.microsoft.com"
    }
    
    r = requests.post(token_url, data=token_data, timeout=30)
    if r.status_code != 200:
        log(f"[ERROR] Authentication failed: {r.status_code} - {r.text}")
        sys.exit(1)
        
    token = r.json()["access_token"]
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }
    log("[SUCCESS] Authenticated successfully!")

    # 2. Get Application Info & check for pending submissions
    app_url = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{app_id}"
    r_app = requests.get(app_url, headers=headers, timeout=30)
    if r_app.status_code != 200:
        log(f"[ERROR] Failed to fetch application info: {r_app.status_code} - {r_app.text}")
        sys.exit(1)

    app_data = r_app.json()
    log(f"[INFO] App Name: {app_data.get('primaryName')}")

    pending = app_data.get("pendingApplicationSubmission")
    if pending:
        sub_id = pending.get("id")
        log(f"[WARN] Found existing pending submission {sub_id}. Reusing/Checking it...")
        sub_url = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{app_id}/submissions/{sub_id}"
        r_sub = requests.get(sub_url, headers=headers, timeout=30)
        sub_data = r_sub.json() if r_sub.status_code == 200 else None
    else:
        log("[INFO] Creating new submission in Partner Center...")
        create_url = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{app_id}/submissions"
        r_create = requests.post(create_url, headers=headers, json={}, timeout=60)
        if r_create.status_code not in (200, 201):
            log(f"[ERROR] Failed to create submission: {r_create.status_code} - {r_create.text}")
            sys.exit(1)
        sub_data = r_create.json()
        sub_id = sub_data["id"]

    log(f"[INFO] Submission ID: {sub_id}")
    file_upload_url = sub_data.get("fileUploadUrl")
    if not file_upload_url:
        log(f"[ERROR] Submission returned no fileUploadUrl: {sub_data}")
        sys.exit(1)

    # 3. Create ZIP archive for Azure Blob upload
    pkg_filename = os.path.basename(pkg_path)
    with tempfile.TemporaryDirectory() as tmpdir:
        zip_path = os.path.join(tmpdir, "package.zip")
        log(f"[INFO] Packing {pkg_filename} into submission ZIP bundle...")
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.write(pkg_path, arcname=pkg_filename)
        
        zip_size = os.path.getsize(zip_path)
        log(f"[INFO] ZIP bundle ready: {zip_size / (1024*1024):.2f} MB")

        # 4. Upload to Azure Blob Storage using BlockBlob REST API
        # file_upload_url contains SAS token e.g. https://...?sv=...
        log(f"[UPLOAD] Uploading bundle to Azure Blob Storage...")
        clean_url = file_upload_url.replace("+", "%2B")
        
        upload_headers = {
            "x-ms-blob-type": "BlockBlob",
            "Content-Type": "application/octet-stream"
        }
        
        # Stream upload with retries
        success = False
        for attempt in range(1, 4):
            try:
                with open(zip_path, "rb") as f:
                    r_upload = requests.put(clean_url, data=f, headers=upload_headers, timeout=300)
                if r_upload.status_code in (200, 201):
                    log("[SUCCESS] Azure Blob upload complete (201 Created)!")
                    success = True
                    break
                else:
                    log(f"[WARN] Upload attempt {attempt} failed: {r_upload.status_code} - {r_upload.text}")
            except Exception as ex:
                log(f"[WARN] Upload attempt {attempt} exception: {ex}")
            time.sleep(3)

        if not success:
            log("[ERROR] Failed to upload package to Azure Blob storage after 3 attempts.")
            sys.exit(1)

    # 5. Update submission package metadata
    log("[INFO] Updating submission package descriptor...")
    sub_data["applicationPackages"] = [
        {
            "fileName": pkg_filename,
            "fileStatus": "PendingUpload",
            "minimumDirectXVersion": "None",
            "minimumSystemRam": "None"
        }
    ]
    
    sub_update_url = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{app_id}/submissions/{sub_id}"
    r_update = requests.put(sub_update_url, headers=headers, json=sub_data, timeout=60)
    if r_update.status_code not in (200, 204):
        log(f"[WARN] Warning updating submission metadata: {r_update.status_code} - {r_update.text}")
    else:
        log("[SUCCESS] Submission metadata updated.")

    # 6. Commit Submission for Certification & Publishing
    log("[COMMIT] Committing submission to Microsoft Store certification...")
    commit_url = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{app_id}/submissions/{sub_id}/commit"
    r_commit = requests.post(commit_url, headers=headers, timeout=60)
    if r_commit.status_code not in (200, 202):
        log(f"[ERROR] Failed to commit submission: {r_commit.status_code} - {r_commit.text}")
        sys.exit(1)

    log("[DONE] SUCCESS! Submission committed to Microsoft Store! It is now in ingestion/certification.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python publish_msstore.py <package_path>")
        sys.exit(1)
        
    pkg = sys.argv[1]
    tenant = os.environ.get("STORE_TENANT_ID")
    cid = os.environ.get("STORE_CLIENT_ID")
    csec = os.environ.get("STORE_CLIENT_SECRET")
    aid = os.environ.get("STORE_APP_ID", "9NZXP5WR803J")
    seller = os.environ.get("SELLER_ID", "94042650")
    
    if not (tenant and cid and csec):
        log("[ERROR] Missing environment variables: STORE_TENANT_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET")
        sys.exit(1)
        
    publish(pkg, tenant, cid, csec, aid, seller)
