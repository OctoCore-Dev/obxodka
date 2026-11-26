using Path = System.IO.Path;

namespace obxodka.Pages.Store;

public sealed partial class ThemeStoreView
{
    private bool MatchesCategoryFilter(ThemeCardModel model) =>
        _activeFilter switch
        {
            "video" => model.HasVideoBackground,
            "anim" => model.HasAnimatedButton || model.HasParticles,
            "sound" => model.HasSounds,
            "shaders" => model.HasScreenOverlay || model.HasReactiveVfx,
            _ => true
        };

    private void RenderThemes(bool animate = true)
    {
        if (_themeManager is null)
        {
            _ = Interlocked.Increment(ref _renderGeneration);
            ThemesContainer.Clear();
            _cardMap.Clear();
            _pendingIcons.Clear();
            _activeDownloadLabels.Clear();
            LabelStatus.IsVisible = false;
            return;
        }

        var installedList = _cachedInstalledThemes;
        Dictionary<string, ThemeManifest> installedMap = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var manifest in installedList)
        {
            installedMap[manifest.Id] = manifest;
        }

        List<ThemeCardModel> models = [];

        if (_currentTab == "installed")
        {
            foreach (var manifest in installedList)
            {
                if (!MatchesSearch(manifest.Name, manifest.Id, manifest.Description, manifest.Author, manifest.Tags))
                {
                    continue;
                }

                var model = BuildInstalledThemeModel(manifest);
                if (MatchesCategoryFilter(model))
                {
                    models.Add(model);
                }
            }
        }
        else if (_currentCatalog is { Themes.Count: > 0 })
        {
            foreach (var item in _currentCatalog.Themes)
            {
                if (!MatchesSearch(item.Name, item.Id, item.Description, item.Author, item.Tags))
                {
                    continue;
                }

                var model = BuildCatalogThemeModel(item, installedMap);
                if (MatchesCategoryFilter(model))
                {
                    models.Add(model);
                }
            }
        }

        if (models.Count == 0)
        {
            _ = Interlocked.Increment(ref _renderGeneration);
            ThemesContainer.Clear();
            _cardMap.Clear();
            _pendingIcons.Clear();
            _activeDownloadLabels.Clear();
            LabelStatus.Text = _currentTab == "installed"
                ? string.IsNullOrWhiteSpace(_searchQuery) && _activeFilter == "all"
                    ? "У вас пока нет установленных тем.\nПерейдите во вкладку 'Сообщество', чтобы выбрать и скачать темы!"
                    : "Ничего не найдено по выбранным фильтрам."
                : _currentCatalog is null || _currentCatalog.Themes.Count == 0
                    ? "Каталог тем временно недоступен или пуст."
                    : "Ничего не найдено по выбранным фильтрам.";
            LabelStatus.IsVisible = true;
            return;
        }

        LabelStatus.IsVisible = false;

        var canUpdateInPlace = _cardMap.Count > 0 &&
                               _cardMap.Count == models.Count &&
                               models.All(m => _cardMap.ContainsKey(m.Id));

        if (canUpdateInPlace)
        {
            var savedScrollY = ThemeScrollView.ScrollY;
            var cardCornerRadius = Application.Current?.Resources.TryGetValue("CardCornerRadius", out var cr) == true &&
                                   cr is CornerRadius themeRadius
                ? themeRadius
                : new CornerRadius(12);
            var borderThickness = Application.Current?.Resources.TryGetValue("CardBorderThickness", out var bt) == true &&
                                  bt is double t
                ? t
                : 1.0;
            var anyActionsChanged = false;

            foreach (var model in models)
            {
                if (!_cardMap.TryGetValue(model.Id, out var holder))
                {
                    continue;
                }

                holder.CardBorder.StrokeThickness = model.IsActive ? Math.Max(2.0, borderThickness) : borderThickness;
                holder.CardBorder.StrokeShape = new RoundRectangle
                {
                    CornerRadius = cardCornerRadius
                };
                holder.CardBorder.SetDynamicResource(Border.StrokeProperty, model.IsActive ? "Primary" : "BorderSubtle");

                var prev = holder.Model;
                var stateChanged = prev.IsActive != model.IsActive ||
                                   prev.IsInstalled != model.IsInstalled ||
                                   prev.HasUpdate != model.HasUpdate ||
                                   !string.Equals(prev.DisplayVersion, model.DisplayVersion, StringComparison.Ordinal) ||
                                   !string.Equals(prev.UpdateVersion, model.UpdateVersion, StringComparison.Ordinal);

                if (stateChanged)
                {
                    anyActionsChanged = true;
                    _ = _activeDownloadLabels.Remove(model.Id);
                    holder.ActionLayout.Children.Clear();
                    PopulateActionLayout(holder.ActionLayout, model);
                }

                _cardMap[model.Id] = (holder.CardBorder, holder.ActionLayout, holder.IconImage, model);
            }

            if (anyActionsChanged && savedScrollY > 2)
            {
                _ = Dispatcher.Dispatch(() =>
                {
                    if (Math.Abs(ThemeScrollView.ScrollY - savedScrollY) > 2)
                    {
                        _ = ThemeScrollView.ScrollToAsync(0, savedScrollY, false);
                    }
                });
            }

            return;
        }

        var generation = Interlocked.Increment(ref _renderGeneration);
        var restoreScrollY = _pendingRestoreScrollY > 2 ? _pendingRestoreScrollY : ThemeScrollView.ScrollY;
        _pendingRestoreScrollY = 0;

        foreach (var (_, _, iconImage, _) in _cardMap.Values)
        {
            iconImage.Source = null;
        }

        ThemesContainer.Clear();
        _cardMap.Clear();
        _pendingIcons.Clear();
        _activeDownloadLabels.Clear();
        _revealedCards.Clear();

        const int initialBatchSize = 6;
        var initialCount = Math.Min(models.Count, restoreScrollY > 2 ? models.Count : initialBatchSize);
        AppendThemeCardBatch(models, 0, initialCount, animate, loadIconsImmediately: restoreScrollY <= 2);

        if (initialCount < models.Count)
        {
            ScheduleRemainingCardBatches(models, initialCount, generation, animate);
        }
        else if (restoreScrollY > 2)
        {
            _ = Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(35), () =>
            {
                if (generation != Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                _ = ThemeScrollView.ScrollToAsync(0, restoreScrollY, false);
                LoadPendingIconsForViewport(restoreScrollY, ThemeScrollView.Height);
            });
        }
    }

    //порционное добавление карточек тем между кадрами без блокировки ui-потока
    private void AppendThemeCardBatch(List<ThemeCardModel> models, int startIndex, int endExclusive, bool animate, bool loadIconsImmediately)
    {
        for (var i = startIndex; i < endExclusive; i++)
        {
            var model = models[i];
            var eagerIcon = loadIconsImmediately && i < 6;
            var (card, actionLayout, iconImage) = CreateThemeCard(model, eagerIcon);
            ThemesContainer.Add(card);
            _cardMap[model.Id] = (card, actionLayout, iconImage, model);
            if (!eagerIcon)
            {
                _pendingIcons.Add((card, iconImage, model));
            }

            if (!animate || i < 4)
            {
                card.Opacity = 1.0;
                card.Scale = 1.0;
                card.TranslationY = 0;
                _ = _revealedCards.Add(card);
            }
        }
    }

    private void ScheduleRemainingCardBatches(List<ThemeCardModel> models, int nextIndex, int generation, bool animate)
    {
        _ = Dispatcher.Dispatch(() =>
        {
            if (generation != Volatile.Read(ref _renderGeneration) || _themeManager is null)
            {
                return;
            }

            const int batchSize = 5;
            var end = Math.Min(models.Count, nextIndex + batchSize);
            AppendThemeCardBatch(models, nextIndex, end, animate, loadIconsImmediately: false);
            LoadPendingIconsForViewport(ThemeScrollView.ScrollY, ThemeScrollView.Height);

            if (end < models.Count)
            {
                ScheduleRemainingCardBatches(models, end, generation, animate);
            }
        });
    }

    private ThemeCardModel BuildInstalledThemeModel(ThemeManifest manifest)
    {
        var folder = Path.Join(_themeManager!.ThemesDirectory, manifest.Id.TrimStart('/', '\\'));
        var isActive = _themeManager.ActiveTheme?.Id == manifest.Id;
        var remoteItem = _currentCatalog?.Themes.FirstOrDefault(t => t.Id == manifest.Id);
        var hasUpdate = remoteItem is not null && ThemeManager.IsVersionNewer(manifest.Version, remoteItem.Version);
        var hasVideoBg = !string.IsNullOrEmpty(manifest.Background?.VideoSource);
        var hasAnimBtn = !string.IsNullOrEmpty(manifest.Decorations?.ButtonVideoIdle)
                         || !string.IsNullOrEmpty(manifest.Decorations?.ButtonVideoActive)
                         || manifest.Decorations?.ButtonImageIdle?.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) == true;
        var hasOverlay = !string.IsNullOrEmpty(manifest.Vfx?.ScreenOverlay)
                         && !manifest.Vfx.ScreenOverlay.Equals("none", StringComparison.OrdinalIgnoreCase);
        var hasReactive = manifest.Vfx?.InteractiveParticles == true
                          || manifest.Vfx?.TrafficReactive == true
                          || manifest.Vfx?.ParallaxIntensity > 0
                          || manifest.Vfx?.ActiveOverrides is not null;
        var hasFrames = !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrame)
                        || !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrameMobile)
                        || !string.IsNullOrEmpty(manifest.Decorations?.CardFrame);

        return new ThemeCardModel(
            manifest.Id,
            manifest.Name,
            manifest.Author,
            manifest.Version,
            manifest.Description,
            manifest.Core.Sounds is not null,
            manifest.Vfx?.Particles is not null && manifest.Vfx.Particles != "none",
            hasFrames,
            hasVideoBg,
            hasAnimBtn,
            hasOverlay,
            hasReactive,
            remoteItem?.IconUrl,
            Path.Join(folder, "icon.png"),
            remoteItem?.PreviewUrl,
            Path.Join(folder, "preview.png"),
            manifest.Core.Colors,
            true,
            isActive,
            hasUpdate,
            remoteItem?.Version,
            false);
    }

    private ThemeCardModel BuildCatalogThemeModel(ThemeCatalogItem item, Dictionary<string, ThemeManifest> installedMap)
    {
        var isInstalled = installedMap.TryGetValue(item.Id, out var installedManifest);
        var isActive = _themeManager!.ActiveTheme?.Id == item.Id;
        var installedVersion = isInstalled ? _themeManager.GetInstalledThemeVersion(item.Id) : null;
        var displayVersion = !string.IsNullOrEmpty(installedVersion) ? installedVersion : item.Version;
        var hasUpdate = isInstalled && _themeManager.IsUpdateAvailable(item.Id, item.Version);
        var folder = isInstalled ? Path.Join(_themeManager.ThemesDirectory, item.Id.TrimStart('/', '\\')) : null;

        return new ThemeCardModel(
            item.Id,
            item.Name,
            item.Author,
            displayVersion,
            item.Description,
            item.Features.HasSounds,
            item.Features.HasParticles,
            item.Features.HasFrames,
            item.Features.HasVideo,
            item.Features.HasButtonVideo || item.Features.HasIdolButton,
            item.Features.HasScreenOverlay,
            item.Features.HasReactiveVfx,
            item.IconUrl,
            folder is not null ? Path.Join(folder, "icon.png") : null,
            item.PreviewUrl,
            folder is not null ? Path.Join(folder, "preview.png") : null,
            installedManifest?.Core.Colors,
            isInstalled,
            isActive,
            hasUpdate,
            item.Version,
            true);
    }

    private bool MatchesSearch(string name, string id, string? desc, string author, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return true;
        }

        var q = _searchQuery.Trim();
        return name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || id.Contains(q, StringComparison.OrdinalIgnoreCase)
               || author.Contains(q, StringComparison.OrdinalIgnoreCase)
               || desc is not null && desc.Contains(q, StringComparison.OrdinalIgnoreCase)
               || tags is not null && tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ThemeCardModel(
        string Id,
        string Name,
        string Author,
        string DisplayVersion,
        string? Description,
        bool HasSounds,
        bool HasParticles,
        bool HasFrames,
        bool HasVideoBackground,
        bool HasAnimatedButton,
        bool HasScreenOverlay,
        bool HasReactiveVfx,
        string? RemoteIconUrl,
        string? LocalIconPath,
        string? RemotePreviewUrl,
        string? LocalPreviewPath,
        ThemeColors? Colors,
        bool IsInstalled,
        bool IsActive,
        bool HasUpdate,
        string? UpdateVersion,
        bool IsCommunity
    );
}
