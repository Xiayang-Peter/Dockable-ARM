using System.IO;
using System.Media;
using System.Windows;

namespace Dockable;

/// <summary>
/// Plays short UI sound effects (WAV files embedded under Assets/Sounds as WPF resources) via
/// <see cref="SoundPlayer"/> — reliable for WAV on every Windows install, and resource-backed so the
/// app stays a single portable exe. One cached, pre-loaded player per sound (kept alive so its buffer
/// stays valid); re-playing restarts it, and different sounds can overlap. Failures are swallowed — a
/// missing sound should never break the action that triggered it.
/// </summary>
internal static class Sounds
{
    public const string EmptyTrash = "empty-trash.wav";
    public const string DragToTrash = "drag-to-trash.wav";
    public const string Remove = "remove.wav";

    private static readonly Dictionary<string, SoundPlayer> Players = new();

    public static void Play(string fileName)
    {
        try
        {
            if (!Players.TryGetValue(fileName, out var player))
            {
                var info = Application.GetResourceStream(
                    new Uri($"pack://application:,,,/Assets/Sounds/{fileName}", UriKind.Absolute));
                if (info is null)
                    return;

                // Copy the resource into an in-memory stream the player keeps for the app's lifetime
                // (cached below), so replays don't depend on the original resource stream's position.
                var ms = new MemoryStream();
                using (info.Stream)
                    info.Stream.CopyTo(ms);
                ms.Position = 0;

                player = new SoundPlayer(ms);
                player.Load(); // decode into memory once
                Players[fileName] = player;
            }
            player.Play(); // async on a background thread; restarts if already playing
        }
        catch
        {
            // Sound is a nicety; never let it disrupt the action.
        }
    }
}
