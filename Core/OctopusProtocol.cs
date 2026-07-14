namespace obxodka.Core;

internal static class OctopusProtocol
{
    public static async Task PumpTrafficAsync(Stream input, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
