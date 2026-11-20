using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using BasHarness.Core;
using ThunderRoad;
using UnityEngine;
using UnityEngine.Rendering;

namespace BasHarness
{
    /// <summary>
    /// Films a stretch of a scenario (record-start … record-stop) from up to three cameras: "side", 3 m beside the line between
    /// the player and the spawned NPC so both are in view; "first", the player's own view (a copy of the head camera);
    /// "npc", around the NPC's root part as screenshot of=npc (distance, rise, angle), for an NPC on the ground.
    /// Pictures are read back from the GPU without waiting (AsyncGPUReadback) and appended by a background thread to one
    /// raw RGBA file per view, at a fixed frame rate in real time (FilmFrames); the runner turns them into videos.
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
        private float _startedAt;
        private int _lastRequested = -1;
        private int _repeated;
        private int _pending;
        private bool _running;

        public Recorder(string name, string dir, int fps, int width, int height, string[] views, Func<Creature> npc,
            float npcDistance, float npcRise, float npcAngle)
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
                _files[i] = new FileStream(Path.Combine(dir, name + "-" + views[i] + ".rgba"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                _lastWritten[i] = -1;
            }
            _writer = new Thread(Write) { IsBackground = true, Name = "BasHarness film writer" };
            _writer.Start();
        }

        public string Describe => _views.Length + " view(s) " + string.Join(", ", _views) + " at " + _width + "x" + _height + ", " + _fps
            + " fps, readback " + (_async ? "async" : "sync");

        public System.Collections.IEnumerator Run()
        {
            _running = true;
            _startedAt = Time.realtimeSinceStartup;
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
            File.WriteAllText(Path.Combine(_dir, _name + ".film.json"), FilmFrames.Sidecar(_name, _width, _height, _fps, _views, frames, _repeated));
            return _name + ": " + frames + " frames (" + (frames / (float)_fps).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " s), " + _repeated + " repeated because the game was slower than " + _fps + " fps";
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
                        cam.transform.SetPositionAndRotation(head.transform.position, head.transform.rotation);
                        cam.fieldOfView = 90f;
                        cam.cullingMask = head.cullingMask;
                    }
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
}
