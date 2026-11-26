using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using BasHarness.Core;
using ThunderRoad;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BasHarness
{
    /// <summary>
    /// Films a stretch of a scenario (record-start … record-stop) from up to three cameras: "side", 3 m beside the line between
    /// the player and the spawned NPC so both are in view; "first", from the player's eyes looking at the right hand (the head camera's place and layers);
    /// "npc", around the NPC's root part as screenshot of=npc (distance, rise, angle), for an NPC on the ground; "hand", a
    /// close-up of the player's right hand and blade from the side, for wrist gestures.
    /// Pictures are read back from the GPU without waiting (AsyncGPUReadback) and appended by a background thread to one
    /// raw RGBA file per view, at a fixed frame rate in real time (FilmFrames); the runner turns them into videos.
    /// With sound (audio=true) the game's mix as the player hears it comes along from the same moment on (SoundTap).
    /// </summary>
    internal sealed class Recorder
    {
        private readonly string _name;
        private readonly string _dir;
        private readonly int _fps;
        private readonly int _width;
        private readonly int _height;
        private readonly string[] _views;
        private readonly Func<Creature> _npc;
        private readonly float _npcDistance;
        private readonly float _npcRise;
        private readonly float _npcAngle;
        private readonly Camera[] _cameras;
        private readonly RenderTexture[] _textures;
        private readonly FileStream[] _files;
        private readonly int[] _lastWritten;
        private readonly BlockingCollection<(int View, int Slot, byte[] Data)> _queue = new BlockingCollection<(int, int, byte[])>();
        private readonly Thread _writer;
        private readonly bool _async;
        private readonly SoundTap _sound;
        private float _startedAt;
        private int _lastRequested = -1;
        private int _repeated;
        private int _pending;
        private bool _running;

        public Recorder(string name, string dir, int fps, int width, int height, string[] views, Func<Creature> npc,
            float npcDistance, float npcRise, float npcAngle, bool audio)
        {
            _npcDistance = npcDistance;
            _npcRise = npcRise;
            _npcAngle = npcAngle;
            _name = name;
            _dir = dir;
            _fps = fps;
            _width = width;
            _height = height;
            _views = views;
            _npc = npc;
            _async = SystemInfo.supportsAsyncGPUReadback;
            Directory.CreateDirectory(dir);
            _cameras = new Camera[views.Length];
            _textures = new RenderTexture[views.Length];
            _files = new FileStream[views.Length];
            _lastWritten = new int[views.Length];
            for (var i = 0; i < views.Length; i++)
            {
                _textures[i] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                var go = new GameObject("BasHarnessFilm-" + views[i]);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cameras[i] = go.AddComponent<Camera>();
                _cameras[i].targetTexture = _textures[i];
                _cameras[i].nearClipPlane = 0.03f;
                if (views[i] != "first" && views[i] != "hand" && HeadRenderer.Index >= 0)
                {
                    _cameras[i].GetUniversalAdditionalCameraData().SetRenderer(HeadRenderer.Index);
                }
                _files[i] = new FileStream(Path.Combine(dir, name + "-" + views[i] + ".rgba"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                _lastWritten[i] = -1;
            }
            _writer = new Thread(Write) { IsBackground = true, Name = "BasHarness film writer" };
            _writer.Start();
            _sound = audio ? SoundTap.Attach(Path.Combine(dir, name + ".f32")) : null;
        }

        public string Describe => _views.Length + " view(s) " + string.Join(", ", _views) + " at " + _width + "x" + _height + ", " + _fps
            + " fps, readback " + (_async ? "async" : "sync") + "; " + HeadRenderer.Found + (_sound != null ? "; sound " + _sound.Describe : string.Empty);

        public System.Collections.IEnumerator Run()
        {
            _running = true;
            _startedAt = Time.realtimeSinceStartup;
            _sound?.Arm();
            while (_running)
            {
                yield return null;
                Place();
                yield return new WaitForEndOfFrame();
                if (!_running)
                {
                    break;
                }
                var slot = FilmFrames.Slot(Time.realtimeSinceStartup - _startedAt, _fps);
                if (slot <= _lastRequested)
                {
                    continue;
                }
                _lastRequested = slot;
                for (var i = 0; i < _views.Length; i++)
                {
                    Capture(i, slot);
                }
            }
        }

        /// <summary>Stops filming, waits for the last pictures, closes the files and writes the description for the runner.</summary>
        public string Stop()
        {
            _running = false;
            var sound = _sound?.Stop();
            if (_async)
            {
                AsyncGPUReadback.WaitAllRequests();
            }
            var waitUntil = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < waitUntil)
            {
                Thread.Sleep(10);
            }
            _queue.CompleteAdding();
            _writer.Join(TimeSpan.FromSeconds(30));
            foreach (var f in _files)
            {
                f.Dispose();
            }
            for (var i = 0; i < _views.Length; i++)
            {
                UnityEngine.Object.Destroy(_cameras[i].gameObject);
                _textures[i].Release();
                UnityEngine.Object.Destroy(_textures[i]);
            }
            var frames = _lastWritten.Length > 0 ? _lastWritten[0] + 1 : 0;
            var heard = sound != null && sound.Samples > 0;
            File.WriteAllText(Path.Combine(_dir, _name + ".film.json"), FilmFrames.Sidecar(_name, _width, _height, _fps, _views, frames, _repeated,
                heard ? sound.Rate : 0, heard ? sound.Channels : 0));
            return _name + ": " + frames + " frames (" + (frames / (float)_fps).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " s), " + _repeated + " repeated because the game was slower than " + _fps + " fps" + (sound != null ? "; sound: " + sound.Summary : string.Empty);
        }

        private void Capture(int view, int slot)
        {
            if (_async)
            {
                Interlocked.Increment(ref _pending);
                AsyncGPUReadback.Request(_textures[view], 0, TextureFormat.RGBA32, request =>
                {
                    try
                    {
                        if (!request.hasError)
                        {
                            _queue.Add((view, slot, request.GetData<byte>().ToArray()));
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                });
                return;
            }
            var previous = RenderTexture.active;
            RenderTexture.active = _textures[view];
            var picture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            picture.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            RenderTexture.active = previous;
            _queue.Add((view, slot, picture.GetRawTextureData()));
            UnityEngine.Object.Destroy(picture);
        }

        private void Write()
        {
            foreach (var (view, slot, data) in _queue.GetConsumingEnumerable())
            {
                var times = FilmFrames.Writes(slot, _lastWritten[view]);
                for (var k = 0; k < times; k++)
                {
                    _files[view].Write(data, 0, data.Length);
                }
                if (times > 0)
                {
                    if (view == 0)
                    {
                        _repeated += times - 1;
                    }
                    _lastWritten[view] = slot;
                }
            }
        }

        /// <summary>Side: 3 m beside the middle between player and NPC, 1.2 m up. First: where the player's head camera is.</summary>
        private void Place()
        {
            var player = Player.local;
            var body = player != null ? player.creature : null;
            var npc = _npc();
            for (var i = 0; i < _views.Length; i++)
            {
                var cam = _cameras[i];
                if (_views[i] == "first")
                {
                    var head = player != null && player.head != null ? player.head.cam : null;
                    if (head != null)
                    {
                        // From the player's eyes, looking at the right hand (a player watches the blades; the mock
                        // headset only ever looks straight ahead, so the strike at hip height was out of the picture).
                        var lookAt = body != null && body.handRight != null ? body.handRight.transform.position : head.transform.position + head.transform.forward;
                        cam.transform.position = head.transform.position;
                        cam.transform.LookAt(Vector3.Lerp(lookAt, npc != null ? npc.transform.position + (Vector3.up * 1.0f) : lookAt, 0.3f));
                        cam.fieldOfView = 90f;
                        cam.cullingMask = head.cullingMask;
                    }
                    continue;
                }
                if (_views[i] == "hand")
                {
                    // Close-up of the player's right hand and its blade, from the side of the player-NPC line, so a
                    // roll of the wrist shows (the other views are too far away for it).
                    var hand = body != null ? body.handRight : null;
                    if (hand == null)
                    {
                        continue;
                    }
                    var grip = hand.grabbedHandle != null ? hand.grabbedHandle.transform.position : hand.transform.position;
                    var item = hand.grabbedHandle != null ? hand.grabbedHandle.item : null;
                    var tip = item == null ? grip : item.collisionHandlers.SelectMany(h => h.damagers).Where(d => d != null)
                        .Select(d => d.transform.position).OrderByDescending(p => (p - grip).sqrMagnitude).DefaultIfEmpty(grip).First();
                    var focus = (grip * 0.6f) + (tip * 0.4f);
                    var toNpc = npc != null ? npc.transform.position - body.transform.position : body.transform.forward;
                    toNpc.y = 0f;
                    var side = Vector3.Cross(Vector3.up, toNpc.sqrMagnitude > 1e-4f ? toNpc.normalized : body.transform.forward);
                    // From above the side: level with the hand the player's own hip and leg filled the picture (2026-11-20).
                    cam.fieldOfView = 55f;
                    cam.transform.position = focus + (side * 0.55f) + (Vector3.up * 0.65f);
                    cam.transform.LookAt(focus);
                    continue;
                }
                if (_views[i] == "npc")
                {
                    // As screenshot of=npc: around the NPC's root part, from the player's side turned by the angle.
                    if (npc == null || npc.ragdoll == null || npc.ragdoll.rootPart == null)
                    {
                        continue;
                    }
                    var target = npc.ragdoll.rootPart.transform.position;
                    var toPlayer = body != null ? body.transform.position - target : Vector3.forward;
                    toPlayer.y = 0f;
                    toPlayer = toPlayer.sqrMagnitude > 1e-4f ? toPlayer.normalized : Vector3.forward;
                    toPlayer = Quaternion.Euler(0f, _npcAngle, 0f) * toPlayer;
                    cam.fieldOfView = 60f;
                    cam.transform.position = target + (toPlayer * _npcDistance) + (Vector3.up * _npcRise);
                    cam.transform.LookAt(target);
                    continue;
                }
                if (body == null)
                {
                    continue;
                }
                var from = body.transform.position;
                var to = npc != null ? npc.transform.position : from + (body.transform.forward * 1.5f);
                var line = to - from;
                line.y = 0f;
                var across = Vector3.Cross(Vector3.up, line.sqrMagnitude > 1e-4f ? line.normalized : body.transform.forward);
                var middle = (from + to) / 2f;
                cam.fieldOfView = 55f;
                cam.transform.position = middle + (across * 3.0f) + (Vector3.up * 1.2f);
                cam.transform.LookAt(middle + (Vector3.up * 0.95f));
            }
        }
    }

    /// <summary>
    /// The game's sound for a film: the mix the active AudioListener hears (every source and mixer group, after the
    /// master), taken in OnAudioFilterRead on the listener's own object and appended by a background thread to one raw
    /// file of 32-bit float samples, interleaved. The sound passes on untouched. Samples count from Arm (the film's
    /// first moment) to Stop; the audio thread hands them over in blocks of a few milliseconds.
    /// </summary>
    internal sealed class SoundTap : MonoBehaviour
    {
        private readonly BlockingCollection<float[]> _blocks = new BlockingCollection<float[]>();
        private FileStream _file;
        private Thread _writer;
        private volatile bool _armed;
        private long _samples;
        private int _channels;

        public string Describe { get; private set; } = "off";

        public int Rate { get; private set; }

        public int Channels => Volatile.Read(ref _channels);

        public long Samples => Interlocked.Read(ref _samples);

        public string Summary => Samples == 0
            ? "nothing heard (" + Describe + ")"
            : (Samples / (double)Math.Max(1, Rate) / Math.Max(1, Channels)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " s at " + Rate + " Hz, " + Channels + " channel(s)";

        /// <summary>Puts a tap on the object of the first active and enabled AudioListener; null when there is none (the film stays silent).</summary>
        public static SoundTap Attach(string path)
        {
            var listener = UnityEngine.Object.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l.isActiveAndEnabled);
            if (listener == null)
            {
                Debug.Log("[BasHarness] film sound: no active AudioListener, the film stays silent");
                return null;
            }
            var tap = listener.gameObject.AddComponent<SoundTap>();
            tap.Rate = AudioSettings.outputSampleRate;
            tap._file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            tap._writer = new Thread(tap.Write) { IsBackground = true, Name = "BasHarness film sound writer" };
            tap._writer.Start();
            tap.Describe = "from the AudioListener on '" + listener.name + "' at " + tap.Rate + " Hz, listener volume "
                + AudioListener.volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (AudioListener.pause ? ", paused" : string.Empty);
            return tap;
        }

        /// <summary>From now on every sample counts (not Unity's Start, which would arm it a frame later on its own).</summary>
        public void Arm()
        {
            _armed = true;
        }

        /// <summary>Stops taking samples, writes what is left and closes the file; the tap leaves the listener.</summary>
        public SoundTap Stop()
        {
            _armed = false;
            _blocks.CompleteAdding();
            _writer.Join(TimeSpan.FromSeconds(30));
            _file.Dispose();
            Destroy(this);
            return this;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_armed || _blocks.IsAddingCompleted)
            {
                return;
            }
            Volatile.Write(ref _channels, channels);
            var copy = new float[data.Length];
            Array.Copy(data, copy, data.Length);
            try
            {
                _blocks.Add(copy);
            }
            catch (InvalidOperationException)
            {
                // Stop came between the check and the add.
            }
        }

        private void Write()
        {
            var bytes = new byte[0];
            foreach (var block in _blocks.GetConsumingEnumerable())
            {
                var size = block.Length * sizeof(float);
                if (bytes.Length < size)
                {
                    bytes = new byte[size];
                }
                Buffer.BlockCopy(block, 0, bytes, 0, size);
                _file.Write(bytes, 0, size);
                Interlocked.Add(ref _samples, block.Length);
            }
        }
    }

    /// <summary>
    /// The URP renderer that also draws the player's head. The game puts the player's head meshes on the FPVHide layer
    /// (Creature.RefreshRenderers) so the headset does not see them; a camera on the default renderer then shows the
    /// player without a head. This picks the first renderer of the pipeline asset whose opaque layer mask has both the
    /// FPVHide and the Default layer (so it draws the world too); -1 when there is none (then the head stays hidden).
    /// </summary>
    internal static class HeadRenderer
    {
        private static int? _index;

        public static string Found { get; private set; } = "head renderer not looked for";

        public static int Index
        {
            get
            {
                if (_index.HasValue)
                {
                    return _index.Value;
                }
                _index = -1;
                try
                {
                    var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                    var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var list = asset != null && field != null ? field.GetValue(asset) as ScriptableRendererData[] : null;
                    var fpv = 1 << GameManager.GetLayer(LayerName.FPVHide);
                    var world = 1 << LayerMask.NameToLayer("Default");
                    var seen = new System.Text.StringBuilder();
                    for (var i = 0; list != null && i < list.Length; i++)
                    {
                        var mask = list[i] is UniversalRendererData u ? u.opaqueLayerMask.value : 0;
                        seen.Append(i).Append(':').Append(list[i] != null ? list[i].name : "null").Append(" mask 0x").Append(mask.ToString("X")).Append(' ');
                        if (_index < 0 && (mask & fpv) != 0 && (mask & world) != 0)
                        {
                            _index = i;
                        }
                    }
                    Found = "head renderer " + _index + " (renderers " + seen.ToString().Trim() + ")";
                }
                catch (Exception ex)
                {
                    Found = "head renderer lookup failed: " + ex.Message;
                }
                return _index.Value;
            }
        }
    }
}
