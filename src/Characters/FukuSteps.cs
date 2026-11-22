using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// v0.25: her own footsteps. models/&lt;name&gt;/steps/*.wav|ogg play when a foot lands in her walk clip (contact
    /// rising), from a source at that foot, pitch +-5 %, shuffle bag. While her own walk runs, the uncle's shoe timer
    /// (UncleLogicC.shoeAudio, a heavy step every 0.4 s whatever the legs do) plays a silent clip instead.
    /// </summary>
    public partial class FukuDriver
    {
        readonly List<AudioClip> stepClips = new List<AudioClip>();
        readonly List<int> stepBag = new List<int>();
        int lastStep = -1;
        AudioClip[] originalShoe;
        AudioClip[] silentShoe;
        AudioSource stepSource;
        public float StepVolume = 0.7f;
        bool wasFlatL, wasFlatR;

        public void SetupSteps(string dir)
        {
            string sdir = Path.Combine(dir, "steps");
            if (!Directory.Exists(sdir)) return;
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(sdir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".wav" || ext == ".ogg") files.Add(f);
            }
            files.Sort(System.StringComparer.OrdinalIgnoreCase);
            if (files.Count > 0) StartCoroutine(LoadSteps(files));
        }

        System.Collections.IEnumerator LoadSteps(List<string> files)
        {
            foreach (var f in files)
            {
                var www = new WWW(new System.Uri(f).AbsoluteUri);
                yield return www;
                if (!string.IsNullOrEmpty(www.error)) { Debug.LogWarning("[UncleSwap] step " + f + ": " + www.error); continue; }
                var type = Path.GetExtension(f).ToLowerInvariant() == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
                var clip = www.GetAudioClip(false, false, type);
                while (clip != null && clip.loadState == AudioDataLoadState.Loading) yield return null;
                if (clip != null && clip.length > 0.03f) { clip.name = Path.GetFileNameWithoutExtension(f); stepClips.Add(clip); }
            }
            JaLoader.Console.Log("FukuUncle", "Footsteps: " + stepClips.Count + " sounds");
        }

        AudioSource StepSource()
        {
            if (stepSource != null) return stepSource;
            var go = new GameObject("FukuSteps");
            go.transform.SetParent(transform, false);
            stepSource = go.AddComponent<AudioSource>();
            var src = GetComponent<AudioSource>();
            if (src != null)
            {
                // same 3D sound as the uncle's own (distance, rolloff, mixer), so the game's volume settings apply
                stepSource.outputAudioMixerGroup = src.outputAudioMixerGroup;
                stepSource.spatialBlend = src.spatialBlend;
                stepSource.rolloffMode = src.rolloffMode;
                stepSource.minDistance = src.minDistance;
                stepSource.maxDistance = src.maxDistance;
                stepSource.dopplerLevel = 0f;
                stepSource.volume = src.volume;
            }
            stepSource.playOnAwake = false;
            return stepSource;
        }

        /// <summary>Every frame from WalkIK: silence the uncle's shoes while her walk runs, play a step on landing.</summary>
        void UpdateSteps(float cl, float cr, float w)
        {
            var u = GetComponent<UncleLogicC>();
            if (u != null && stepClips.Count > 0)
            {
                if (originalShoe == null) originalShoe = u.shoeAudio;
                if (silentShoe == null) silentShoe = new[] { AudioClip.Create("silence", 64, 1, 44100, false) };
                bool ours = NewWalk && OwnWalk && walkW > 0.05f;
                u.shoeAudio = ours ? silentShoe : originalShoe;
            }
            bool flatL = cl > 0.98f, flatR = cr > 0.98f;
            if (w > 0.5f && stepClips.Count > 0)
            {
                if (flatL && !wasFlatL && legL != null) PlayStep(legL.Ankle.position);
                if (flatR && !wasFlatR && legR != null) PlayStep(legR.Ankle.position);
            }
            wasFlatL = flatL;
            wasFlatR = flatR;
        }

        void PlayStep(Vector3 at)
        {
            if (stepBag.Count == 0)
            {
                for (int i = 0; i < stepClips.Count; i++) stepBag.Add(i);
                for (int i = stepBag.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); int t = stepBag[i]; stepBag[i] = stepBag[j]; stepBag[j] = t; }
                if (stepBag.Count > 1 && stepBag[stepBag.Count - 1] == lastStep) { int t = stepBag[0]; stepBag[0] = stepBag[stepBag.Count - 1]; stepBag[stepBag.Count - 1] = t; }
            }
            int k = stepBag[stepBag.Count - 1];
            stepBag.RemoveAt(stepBag.Count - 1);
            lastStep = k;
            var s = StepSource();
            s.transform.position = at;
            s.pitch = Random.Range(0.95f, 1.05f);
            s.PlayOneShot(stepClips[k], StepVolume * Random.Range(0.85f, 1f));
        }
    }
}
