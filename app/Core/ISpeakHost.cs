using System.Collections.Generic;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

public interface ISpeakHost
{
    Task SpeakAsync(string? text, byte[]? audio, string? emotion, string? expression);

    Task SpeakStreamAsync(string? text, IAsyncEnumerable<byte[]> audioSegments, string? emotion, string? expression);
}