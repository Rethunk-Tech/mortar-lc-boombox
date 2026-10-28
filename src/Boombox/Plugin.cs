using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using ConfigSync;
using MonoMod.RuntimeDetour;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace Boombox;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
[BepInDependency("tech.rethunk.mortar.ConfigSync")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private const string Message = "tech.rethunk.mortar.Boombox";
    // How often the host sends what each boombox plays, and how far a player may drift before catching up.
    private const float SyncEvery = 5f;
    private const float Drift = 1f;

    private static readonly List<Hook> hooks = [];
    private static string[] files = [];
    // Each boombox loads its own copy of a song: a streamed clip plays on one source at a time, so two boomboxes on the
    // same song would take it from each other.
    private static readonly ConditionalWeakTable<BoomboxItem, Dictionary<string, AudioClip>> loaded = new();
    private static readonly ConditionalWeakTable<BoomboxItem, Track> tracks = new();
    private static SyncedEntry<bool> mixGame = null!, nextSong = null!, newOnToggle = null!;
    private static Loader runner = null!;

    private void Awake()
    {
        Log = Logger;
        // Both would set the boombox's songs.
        if (Chainloader.PluginInfos.ContainsKey("com.steven.lethalcompany.boomboxmusic"))
        {
            Log.LogWarning("Custom Boombox Music is installed, so it plays the songs instead");
            return;
        }
        var shared = SyncedConfig.For(this);
        mixGame = shared.Share(Config.Bind("Boombox", "Include game songs", false,
            "Keep the boombox's own songs in the list with the packs' songs."));
        nextSong = shared.Share(Config.Bind("Boombox", "Next song on each press", false,
            "Play the list in order instead of a random song."));
        newOnToggle = shared.Share(Config.Bind("Boombox", "New song when turned on", false,
            "Start a different song each time it's turned on. Off carries on with the song where it stopped."));
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        hooks.Add(new Hook(typeof(BoomboxItem).GetMethod("StartMusic", any), StartMusic));
        hooks.Add(new Hook(typeof(BoomboxItem).GetMethod(nameof(BoomboxItem.PocketItem)), Pocketed));
        hooks.Add(new Hook(typeof(GrabbableObject).GetMethod(nameof(GrabbableObject.EquipItem)), Equipped));
        hooks.Add(new Hook(typeof(GrabbableObject).GetMethod(nameof(GrabbableObject.DiscardItem)), Dropped));
        hooks.Add(new Hook(typeof(GrabbableObject).GetMethod(nameof(GrabbableObject.ItemInteractLeftRight)), Interacted));
        hooks.Add(new Hook(typeof(StartOfRound).GetMethod("Start", any), ShipLoaded));
        files = Files();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} found {files.Length} songs");
        // BepInEx's own object can go when a scene loads, so the loading and syncing run on one that stays.
        var holder = new GameObject("Boombox songs");
        DontDestroyOnLoad(holder);
        holder.hideFlags = HideFlags.HideAndDontSave;
        runner = holder.AddComponent<Loader>();
    }

    // Each pack's mortar/boombox folder. Still read: each pack's Custom Songs folder, as Custom Boombox Fix packs ship
    // them, and the player's own songs where Custom Boombox Music kept them. Sorted, so the boombox's shuffle picks the
    // same song for everyone with the same packs.
    private static string[] Files()
    {
        IEnumerable<string> dirs = Directory.GetDirectories(Paths.PluginPath, "mortar", SearchOption.AllDirectories)
            .Select(dir => Path.Combine(dir, "boombox"))
            .Concat(Directory.GetDirectories(Paths.PluginPath, "Custom Songs", SearchOption.AllDirectories))
            .Append(Path.Combine(Paths.BepInExRootPath, "Custom Songs", "Boombox Music"))
            .Where(Directory.Exists);
        return dirs.SelectMany(dir => Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => Type(f) != AudioType.UNKNOWN)
            .Distinct()
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
    }

    private static AudioType Type(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp3" => AudioType.MPEG,
        ".ogg" => AudioType.OGGVORBIS,
        ".wav" => AudioType.WAV,
        _ => AudioType.UNKNOWN,
    };

    // Unity under Wine misreads a percent-escaped non-ASCII name (Beyoncé.mp3 is a 404), so only the characters a URL
    // can't hold as they are get escaped.
    private static string FileUrl(string file) =>
        "file:///" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23");

    // The game's songs come first when they're in, then the packs', the same list for everyone.
    private static int GameSongs(BoomboxItem boombox) => mixGame.Value || files.Length == 0 ? boombox.musicAudios.Length : 0;

    private static int Count(BoomboxItem boombox) => GameSongs(boombox) + files.Length;

    // Players' lists can differ, so the host names a song instead of pointing at a place in its list.
    private static string KeyOf(BoomboxItem boombox, int index)
    {
        int games = GameSongs(boombox);
        return index < games ? "game:" + boombox.musicAudios[index].name : Path.GetFileName(files[index - games]);
    }

    private static int IndexOf(BoomboxItem boombox, string key)
    {
        for (int i = 0; i < Count(boombox); i++)
        {
            if (KeyOf(boombox, i) == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static void StartMusic(Action<BoomboxItem, bool, bool> orig, BoomboxItem self, bool startMusic, bool pitchDown)
    {
        Track track = tracks.GetOrCreateValue(self);
        if (!startMusic)
        {
            if (self.isPlayingMusic)
            {
                track.Time = self.boomboxAudio.time;
            }
            orig(self, startMusic, pitchDown);
            Name(self, null);
            return;
        }
        self.boomboxAudio.pitch = 1f;
        self.isBeingUsed = true;
        self.isPlayingMusic = true;
        if (track.Index >= 0 && track.Index < Count(self) && !newOnToggle.Value)
        {
            Play(self, track.Index, track.Time);
        }
        else
        {
            Next(self);
        }
    }

    // Random picks walk a shuffle of the whole list, so nothing repeats until everything's played. The shuffle comes
    // from the boombox's own randomizer, seeded the same for everyone. In order, it's the next one in the list.
    private static void Next(BoomboxItem boombox)
    {
        int count = Count(boombox);
        if (count == 0)
        {
            return;
        }
        Track track = tracks.GetOrCreateValue(boombox);
        if (track.Index >= 0)
        {
            track.History.Add(track.Index);
        }
        int pick = nextSong.Value ? (track.Index + 1) % count : Shuffled(boombox, track, count);
        Play(boombox, pick, 0);
    }

    // Go back through what's played. Before the first song, step back through the list and wrap to the end.
    private static int Shuffled(BoomboxItem boombox, Track track, int count)
    {
        if (track.Order.Count != count || track.Position >= count)
        {
            track.Order.Clear();
            track.Order.AddRange(Enumerable.Range(0, count));
            for (int i = count - 1; i > 0; i--)
            {
                int j = boombox.musicRandomizer.Next(0, i + 1);
                (track.Order[i], track.Order[j]) = (track.Order[j], track.Order[i]);
            }
            // A new round shouldn't open with the song that just ended it.
            if (count > 1 && track.Order[0] == track.Index)
            {
                (track.Order[0], track.Order[count - 1]) = (track.Order[count - 1], track.Order[0]);
            }
            track.Position = 0;
        }
        return track.Order[track.Position++];
    }

    private static void Previous(BoomboxItem boombox)
    {
        Track track = tracks.GetOrCreateValue(boombox);
        int count = Count(boombox);
        if (track.History.Count == 0)
        {
            if (count > 0)
            {
                Play(boombox, ((track.Index - 1) % count + count) % count, 0);
            }
            return;
        }
        int last = track.History[^1];
        track.History.RemoveAt(track.History.Count - 1);
        Play(boombox, last, 0);
    }

    private static void Play(BoomboxItem boombox, int index, float time)
    {
        Track track = tracks.GetOrCreateValue(boombox);
        track.Index = index;
        track.Time = time;
        int games = GameSongs(boombox);
        if (index < games)
        {
            Start(boombox, boombox.musicAudios[index], time);
            return;
        }
        string file = files[index - games];
        if (loaded.GetOrCreateValue(boombox).TryGetValue(file, out AudioClip clip))
        {
            Start(boombox, clip, time);
        }
        else
        {
            runner.StartCoroutine(Load(boombox, file, index));
        }
    }

    // A song loads the first time it's picked, so a big pack costs nothing at startup.
    private static IEnumerator Load(BoomboxItem boombox, string file, int index)
    {
        using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(FileUrl(file), Type(file));
        // Streamed, so a long song doesn't sit decoded in memory.
        ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            Log.LogError($"{file}: {request.error}");
            yield break;
        }
        if (boombox == null)
        {
            yield break;
        }
        Dictionary<string, AudioClip> clips = loaded.GetOrCreateValue(boombox);
        if (!clips.TryGetValue(file, out AudioClip clip))
        {
            clip = DownloadHandlerAudioClip.GetContent(request);
            clip.name = Path.GetFileNameWithoutExtension(file);
            clips[file] = clip;
        }
        // Skip it if the boombox has moved on to another song.
        if (boombox != null && boombox.isPlayingMusic && tracks.TryGetValue(boombox, out Track track) && track.Index == index)
        {
            Start(boombox, clip, track.Time);
        }
    }

    private static void Start(BoomboxItem boombox, AudioClip clip, float time)
    {
        boombox.boomboxAudio.clip = clip;
        boombox.boomboxAudio.time = Mathf.Clamp(time, 0, Mathf.Max(0, clip.length - 0.1f));
        boombox.boomboxAudio.Play();
        Name(boombox, clip.name);
    }

    // Looking at the boombox shows the song to everyone (it has no scan node); the holder sees it with the controls.
    private static void Name(BoomboxItem boombox, string? song)
    {
        boombox.customGrabTooltip = song == null ? null : $"Grab : [E]\n{song}";
        if (boombox.playerHeldBy != null && boombox.playerHeldBy == GameNetworkManager.Instance.localPlayerController)
        {
            Tips(boombox);
        }
    }

    private static void Tips(BoomboxItem boombox)
    {
        string[] lines = [.. boombox.itemProperties.toolTips, "Previous song : [Q]", "Next song : [E]"];
        if (boombox.isPlayingMusic && boombox.boomboxAudio.clip != null)
        {
            lines = [.. lines, boombox.boomboxAudio.clip.name];
        }
        HUDManager.Instance.ChangeControlTipMultiple(lines, holdingItem: true, boombox.itemProperties);
    }

    // The game sends Q and E to every player's copy of the item, so everyone changes song together.
    private static void Equipped(Action<GrabbableObject> orig, GrabbableObject self)
    {
        orig(self);
        if (self is BoomboxItem boombox && boombox.playerHeldBy != null)
        {
            boombox.itemProperties.syncInteractLRFunction = true;
            boombox.playerHeldBy.equippedUsableItemQE = true;
            if (boombox.playerHeldBy == GameNetworkManager.Instance.localPlayerController)
            {
                Tips(boombox);
            }
        }
    }

    private static void Dropped(Action<GrabbableObject> orig, GrabbableObject self)
    {
        if (self is BoomboxItem && self.playerHeldBy != null)
        {
            self.playerHeldBy.equippedUsableItemQE = false;
        }
        orig(self);
    }

    private static void Pocketed(Action<BoomboxItem> orig, BoomboxItem self)
    {
        if (self.playerHeldBy != null)
        {
            self.playerHeldBy.equippedUsableItemQE = false;
        }
        orig(self);
    }

    // Q comes in as left, E as right.
    private static void Interacted(Action<GrabbableObject, bool> orig, GrabbableObject self, bool right)
    {
        orig(self, right);
        if (self is BoomboxItem boombox)
        {
            // The button toggles the item's power on the holder's side; the music decides it here.
            boombox.isBeingUsed = boombox.isPlayingMusic;
            if (!boombox.isPlayingMusic)
            {
                return;
            }
            if (right)
            {
                Next(boombox);
            }
            else
            {
                Previous(boombox);
            }
        }
    }

    private static void ShipLoaded(Action<StartOfRound> orig, StartOfRound self)
    {
        orig(self);
        NetworkManager.Singleton?.CustomMessagingManager?.RegisterNamedMessageHandler(Message, Received);
    }

    // The host sends each playing boombox's song and position so anyone who's drifted catches up.
    private static void SendState()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network == null || !network.IsServer || network.CustomMessagingManager == null || network.ConnectedClientsIds.Count < 2)
        {
            return;
        }
        BoomboxItem[] playing = FindObjectsOfType<BoomboxItem>()
            .Where(b => b.isPlayingMusic && b.IsSpawned && tracks.TryGetValue(b, out Track t) && t.Index >= 0 && t.Index < Count(b))
            .ToArray();
        if (playing.Length == 0)
        {
            return;
        }
        using var message = new FastBufferWriter(64, Allocator.Temp, 4096);
        message.WriteValueSafe(playing.Length);
        foreach (BoomboxItem boombox in playing)
        {
            message.WriteValueSafe(boombox.NetworkObjectId);
            message.WriteValueSafe(KeyOf(boombox, tracks.GetOrCreateValue(boombox).Index));
            message.WriteValueSafe(boombox.boomboxAudio.time);
        }
        foreach (ulong client in network.ConnectedClientsIds)
        {
            if (client != NetworkManager.ServerClientId)
            {
                network.CustomMessagingManager.SendNamedMessage(Message, client, message);
            }
        }
    }

    private static void Received(ulong sender, FastBufferReader reader)
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network.IsServer)
        {
            return;
        }
        reader.ReadValueSafe(out int count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out string key);
            reader.ReadValueSafe(out float time);
            if (!network.SpawnManager.SpawnedObjects.TryGetValue(id, out NetworkObject item)
                || !item.TryGetComponent(out BoomboxItem boombox) || !boombox.isPlayingMusic)
            {
                continue;
            }
            Track track = tracks.GetOrCreateValue(boombox);
            int index = IndexOf(boombox, key);
            if (index < 0)
            {
                // A song this player doesn't have: quiet until the host moves on to one they do.
                track.Index = -1;
                boombox.boomboxAudio.Stop();
                Name(boombox, null);
            }
            else if (track.Index != index)
            {
                Play(boombox, index, time);
            }
            else if (boombox.boomboxAudio.clip != null && Mathf.Abs(boombox.boomboxAudio.time - time) > Drift)
            {
                boombox.boomboxAudio.time = Mathf.Clamp(time, 0, Mathf.Max(0, boombox.boomboxAudio.clip.length - 0.1f));
            }
        }
    }

    private sealed class Track
    {
        public int Index = -1;
        public float Time;
        public readonly List<int> History = [];
        public readonly List<int> Order = [];
        public int Position;
    }

    private sealed class Loader : MonoBehaviour
    {
        private float untilSync = SyncEvery;

        private void Update()
        {
            untilSync -= Time.unscaledDeltaTime;
            if (untilSync <= 0)
            {
                untilSync = SyncEvery;
                SendState();
            }
        }
    }
}
