using System;
using HarmonyLib;
using JaLoader;
using UnityEngine;

namespace UmamusumeCompanion
{
    public sealed partial class CompanionMod
    {
        [Serializable]
        sealed class SavedBindings : SerializableDictionary<string, string> { }

        static KeyCode LegacyWheelBinding()
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath,
                @"ModSaves\UmaCommands\UmaCommands_save.json");
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var saved = JsonUtility.FromJson<SavedBindings>(System.IO.File.ReadAllText(path));
                    string value;
                    int code;
                    if (saved != null && saved.TryGetValue("Wheel_Keybind_primary", out value) &&
                        int.TryParse(value, out code) && Enum.IsDefined(typeof(KeyCode), code))
                        return (KeyCode)code;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Could not read old wheel binding: " + e.Message); }
            return KeyCode.V;
        }

        static readonly string[] Labels = { "Выйди из\nмашины", "Иди за мной", "Остановись", "Зайди\nв машину", "Забери ящик" };
        const float Step = 360f / 5;
        const float DeadZone = 0.31f;
        static readonly Color Ink = new Color32(52, 43, 35, 255);
        static readonly Color Enamel = new Color32(222, 207, 171, 255);
        static readonly Color Steel = new Color32(140, 133, 113, 255);
        static readonly Color Oxide = new Color32(137, 55, 38, 255);
        static readonly Color Paper = new Color32(255, 240, 204, 255);
        static readonly Color Muted = new Color32(98, 91, 75, 255);
        static CompanionMod active;
        internal static CompanionMod Current { get; private set; }
        static bool lookPatched;
        Companion companion;
        readonly string[] reasons = new string[Labels.Length];
        KeyCode key;
        Vector2 pointer;
        Camera view;
        MouseLook look;
        static float Radius => Mathf.Min(Screen.height * 0.25f, Screen.width * 0.30f);
        int selected = -1;
        float noticeUntil;
        string notice;
        Texture2D sector, disc;
        GUIStyle label;

        public override void Preload() { PaintSelection.Install(); }

        void StartWheel()
        {
            if (!lookPatched)
            {
                new Harmony("jalopy.umamusumecompanion.look").Patch(AccessTools.Method(typeof(MouseLook), "FixedUpdate"),
                    prefix: new HarmonyMethod(typeof(CompanionMod), nameof(AllowLook)));
                lookPatched = true;
            }
            Current = this;
            key = GetPrimaryKeybind("Wheel");
        }

        // Leave the game's cursor and camera component state under the game's control.
        static bool AllowLook() => active == null;
        public override void OnSettingsLoaded() { key = GetPrimaryKeybind("Wheel"); }
        public override void OnSettingsReset() { key = GetPrimaryKeybind("Wheel"); }

        static bool Playing()
        {
            var menu = MainMenuC.Global;
            return Application.isFocused && Time.timeScale > 0f && menu != null && menu.isPaused == 0 &&
                   !CompanionMod.PanelIsOpen && (JaLoader.Console.Instance == null || !JaLoader.Console.Instance.Visible) &&
                   (menu.map == null || !menu.map.GetComponent<MapLogicC>().isBookOpen);
        }

        void UpdateWheel()
        {
            if (active != this)
            {
                if (!Input.GetKeyDown(key) || !Playing()) return;
                var driver = FindObjectOfType<CompanionDriver>();
                companion = driver == null ? null : new Companion(driver);
                if (companion == null || !companion.Present) { Notify("Умы сейчас нет рядом"); return; }
                view = Camera.main;
                look = MainMenuC.Global.GetComponent<MouseLook>();
                if (view == null || look == null) return;
                pointer = Vector2.zero;
                selected = -1;
                notice = null;
                active = this;
            }
            else
            {
                if (!Playing() || !companion.Present || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                selected = HitTest(pointer);
                if (!Input.GetKey(key))
                {
                    Close();
                    if (Input.GetKeyUp(key) && selected >= 0)
                    {
                        try { Notify(companion.Run((Command)selected)); }
                        catch (Exception e) { Debug.LogException(e); Notify("Не удалось выполнить команду"); }
                    }
                    return;
                }
            }
            for (int i = 0; i < Labels.Length; i++) reasons[i] = companion.Unavailable((Command)i);
        }

        void FixedUpdate()
        {
            if (active != this || view == null || look == null || Radius <= 0f) return;
            // MouseLook samples angular motion on each physics tick. Convert its degrees
            // to screen displacement at the crosshair using the current camera projection.
            float pixelsPerRadian = view.pixelHeight * 0.5f / Mathf.Tan(view.fieldOfView * Mathf.Deg2Rad * 0.5f);
            var degrees = new Vector2(Input.GetAxis("Mouse X") * look.sensitivityX,
                                      -Input.GetAxis("Mouse Y") * look.sensitivityY);
            pointer = Vector2.ClampMagnitude(pointer + degrees * (Mathf.Deg2Rad * pixelsPerRadian / Radius), 1f);
        }

        internal static int HitTest(Vector2 point)
        {
            if (point.sqrMagnitude < DeadZone * DeadZone) return -1;
            float angle = Mathf.Atan2(point.x, -point.y) * Mathf.Rad2Deg;
            return Mathf.FloorToInt(Mathf.Repeat(angle + Step / 2f, 360f) / Step) % Labels.Length;
        }

        void Close() { if (active == this) active = null; }
        void OnApplicationFocus(bool focus) { if (!focus) Close(); }
        public override void OnDisable() { Close(); notice = null; }
        public override void OnDestroy()
        {
            Close();
            if (Current == this) Current = null;
            if (sector != null) Destroy(sector);
            if (disc != null) Destroy(disc);
        }
        internal void Notify(string message, float seconds = 2.5f) { notice = message; noticeUntil = Time.unscaledTime + seconds; }

        // One white annular slice, rotated and tinted for all five commands.
        static Texture2D Mask(bool slice)
        {
            const int size = 384;
            var pixels = new Color32[size * size];
            float halfAngle = Step * 0.5f * Mathf.Deg2Rad;
            float sin = Mathf.Sin(halfAngle), cos = Mathf.Cos(halfAngle);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size / 2f, dy = y + 0.5f - size / 2f;
                float radius = Mathf.Sqrt(dx * dx + dy * dy);
                float edge = size / 2f - 1f - radius;
                if (slice) edge = Mathf.Min(edge, Mathf.Min(radius - size * DeadZone / 2f, dy * sin - Mathf.Abs(dx) * cos - 1f));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(edge)));
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        void Text(Vector2 center, float width, float height, string value, int size, Color color)
        {
            label.fontSize = size;
            label.normal.textColor = color;
            GUI.Label(new Rect(center.x - width / 2, center.y - height / 2, width, height), value, label);
        }

        void Face(Vector2 center, float radius, Color tint)
        {
            GUI.color = tint;
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, 2f * radius, 2f * radius), disc);
        }

        static void Paint(Rect rect, Color tint)
        {
            GUI.color = tint;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }

        void OnGUI()
        {
            bool open = active == this;
            if (Event.current.type != EventType.Repaint || (!open && (notice == null || Time.unscaledTime >= noticeUntil))) return;
            if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold, padding = new RectOffset(), wordWrap = false };
            float radius = Radius;
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            int font = Mathf.Max(10, Mathf.RoundToInt(radius * 0.087f));
            var savedColor = GUI.color;
            var matrix = GUI.matrix;
            int depth = GUI.depth;
            GUI.depth = -100;
            GUI.color = Color.white;
            if (open)
            {
                if (sector == null) { sector = Mask(true); disc = Mask(false); }
                var rect = new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2);
                // Pressed metal bezel, recessed dial and enamel sectors.
                Face(center + Vector2.up * radius * 0.018f, radius * 1.055f, Ink);
                Face(center, radius * 1.048f, Steel);
                Face(center, radius * 1.021f, Ink);
                for (int i = 0; i < Labels.Length; i++)
                {
                    GUIUtility.RotateAroundPivot(i * Step, center);
                    bool available = reasons[i] == null;
                    GUI.color = i == selected ? (available ? Oxide : Muted) : available ? Enamel : new Color32(187, 178, 152, 255);
                    GUI.DrawTexture(rect, sector);
                    // Each sector has one major division flanked by four pairs of minor ticks.
                    for (int tick = -4; tick <= 4; tick++)
                    {
                        GUI.matrix = matrix;
                        GUIUtility.RotateAroundPivot(i * Step + tick * Step / 10f, center);
                        float width = radius * (tick == 0 ? 0.017f : 0.009f);
                        Paint(new Rect(center.x - width / 2f, center.y - radius * 0.965f, width,
                            radius * (tick == 0 ? 0.078f : 0.038f)), i == selected ? Paper : Muted);
                    }
                    GUI.matrix = matrix;
                    float a = i * Step * Mathf.Deg2Rad;
                    var pos = center + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * radius * 0.67f;
                    GUI.color = Color.white;
                    Text(pos, radius * 0.82f, radius * 0.35f, Labels[i], font,
                        i == selected ? Paper : available ? Ink : Muted);
                }
                Face(center, radius * 0.30f, Steel);
                Face(center, radius * 0.279f, Ink);
                if (pointer.sqrMagnitude > 0.001f)
                {
                    GUIUtility.RotateAroundPivot(Mathf.Atan2(pointer.x, -pointer.y) * Mathf.Rad2Deg, center);
                    // The short needle stays on the hub so it never covers a command.
                    Paint(new Rect(center.x - radius * 0.012f, center.y - radius * 0.309f,
                        radius * 0.024f, radius * 0.104f), Paper);
                    GUI.matrix = matrix;
                    var p = center + pointer * (radius - 4f);
                    Face(p, Mathf.Max(3f, radius * 0.023f), Ink);
                    Face(p, Mathf.Max(2f, radius * 0.013f), Paper);
                }
                GUI.color = Color.white;
                Text(center, radius * 0.55f, radius * 0.25f, "УМА", font + 2, Paper);
            }
            string hint = !open ? notice : selected < 0 ? "Отпусти в центре для отмены" :
                          reasons[selected] ?? ("Отпусти " + key + ", чтобы дать команду");
            label.fontSize = font;
            float w = Mathf.Min(Screen.width - 12f, label.CalcSize(new GUIContent(hint)).x + 44f);
            var hintCenter = center + Vector2.up * (radius * 1.07f + font * 1.4f);
            var plate = new Rect(hintCenter.x - w / 2, hintCenter.y - font, w, font * 2);
            Paint(plate, Ink);
            Paint(new Rect(plate.x + 2, plate.y + 2, plate.width - 4, plate.height - 4), Enamel);
            if (disc != null)
            {
                Face(new Vector2(plate.x + 8, hintCenter.y), 2f, Muted);
                Face(new Vector2(plate.xMax - 8, hintCenter.y), 2f, Muted);
            }
            GUI.color = Color.white;
            Text(hintCenter, Screen.width - 12f, font * 2f, hint, font, Ink);
            GUI.color = savedColor;
            GUI.depth = depth;
        }
    }
}
