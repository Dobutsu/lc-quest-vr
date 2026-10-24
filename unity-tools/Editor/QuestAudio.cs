using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LCQuest.Editor
{
    /// <summary>
    /// The game loads every clip decompressed (PCM): 1,103 clips, 271 MB on the Quest, which sits at the memory limit
    /// (the system kills apps, then the game). On Android, clips longer than a couple of seconds stay Vorbis in memory
    /// and are decoded as they play; the short, frequent ones (footsteps, hits) stay decompressed. Nothing in the game
    /// reads samples from its clips (GetData only on the microphone's own clip).
    /// </summary>
    internal static class QuestAudio
    {
        private const float CompressedFrom = 2f;  // seconds

        public static void Apply()
        {
            var changed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in AssetDatabase.FindAssets("t:AudioClip").Select(AssetDatabase.GUIDToAssetPath))
                {
                    if (AssetImporter.GetAtPath(path) is not AudioImporter importer) continue;
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip == null || clip.length < CompressedFrom) continue;
                    var settings = importer.GetOverrideSampleSettings("Android");
                    if (importer.ContainsSampleSettingsOverride("Android") && settings.loadType == AudioClipLoadType.CompressedInMemory)
                        continue;
                    settings = importer.defaultSampleSettings;
                    settings.loadType = AudioClipLoadType.CompressedInMemory;
                    settings.compressionFormat = AudioCompressionFormat.Vorbis;
                    settings.quality = 0.6f;
                    importer.SetOverrideSampleSettings("Android", settings);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[QuestAudio] {changed} clips of {CompressedFrom} s or more kept compressed on Android");
        }
    }
}
