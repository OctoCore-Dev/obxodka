namespace obxodka.Core;

public static class OctopusProtocol
{
    public static async Task<long> PumpTrafficAsync(Stream input, Stream output, CancellationToken ct)
    {
        var reader = PipeReader.Create(input);
        var writer = PipeWriter.Create(output);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                if (!buffer.IsEmpty)
                {
                    foreach (var segment in buffer)
                    {
                        writer.Write(segment.Span);
                        totalBytes += segment.Length;
                    }
                    _ = await writer.FlushAsync(ct);
                }
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch { }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
        return totalBytes;
    }
}
