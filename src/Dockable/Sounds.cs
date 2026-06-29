using System.IO;
using System.Media;

namespace Dockable;

/// <summary>
/// Plays short UI sound effects (WAV files bundled under Assets/Sounds, copied next to the exe) via
/// <see cref="SoundPlayer"/> — reliable for WAV on every Windows install. One cached, pre-loaded player
/// per sound (kept alive so its buffer stays valid); re-playing restarts it, and different sounds can
/// overlap. Failures are swallowed — a missing file should never break the action that triggered it.
/// </summary>
internal static class Sounds
{
    public const string EmptyTrash = "empty-trash.wav";
    public const string DragToTrash = "drag-to-trash.wav";
    public const string Remove = "remove.wav";

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
    private static readonly Dictionary<string, SoundPlayer> Players = new();

    public static void Play(string fileName)
    {
        try
        {
            if (!Players.TryGetValue(fileName, out var player))
            {
                string path = Path.Combine(Dir, fileName);
                if (!File.Exists(path))
                    return;
                player = new SoundPlayer(path);
                player.Load(); // load into memory once
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
