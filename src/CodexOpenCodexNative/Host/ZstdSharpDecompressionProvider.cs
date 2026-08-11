using Microsoft.AspNetCore.RequestDecompression;

namespace CodexOpenCodexNative.Host;

public sealed class ZstdSharpDecompressionProvider : IDecompressionProvider
{
    public Stream GetDecompressionStream(Stream inputStream) =>
        new ZstdSharp.DecompressionStream(inputStream, leaveOpen: true);
}
