using System;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace LCQuest.Editor
{
    /// <summary>
    /// Development builds (and -adbLaunch release builds, for GPU traces) only: declares optional hand tracking. A VR app that needs controllers is stopped at launch by
    /// the system's "controllers required" check while the controllers sleep, which is always the case when the build is
    /// started over adb with nobody wearing the headset (scripts/quest-test.sh). With hand tracking declared, the
    /// check lets it start. LCVR has no hand input, so release builds leave it out.
    /// </summary>
    public class DevHandTrackingManifest : IPostGenerateGradleAndroidProject
    {
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 1;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("-developmentBuild") && !args.Contains("-adbLaunch"))
                return;

            // path is the unityLibrary module; its manifest is merged into the app's
            var manifestPath = Path.Combine(path, "src/main/AndroidManifest.xml");
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var manifest = (XmlElement)doc.SelectSingleNode("/manifest");
            var application = (XmlElement)manifest.SelectSingleNode("application");

            Add(manifest, "uses-permission", ("name", "com.oculus.permission.HAND_TRACKING"));
            Add(manifest, "uses-feature", ("name", "oculus.software.handtracking"), ("required", "false"));
            Add(application, "meta-data", ("name", "com.oculus.handtracking.frequency"), ("value", "HIGH"));
            Add(application, "meta-data", ("name", "com.oculus.handtracking.version"), ("value", "V2.0"));
            doc.Save(manifestPath);
            Debug.Log($"[QuestBuild] optional hand tracking declared (development build): {Path.GetFullPath(manifestPath)}");
        }

        private static void Add(XmlElement parent, string tag, params (string name, string value)[] attributes)
        {
            var name = attributes[0].value;
            if (parent.SelectNodes(tag).Cast<XmlElement>().Any(e => e.GetAttribute("name", AndroidNs) == name))
                return;
            var element = parent.OwnerDocument.CreateElement(tag);
            foreach (var (key, value) in attributes)
                element.SetAttribute(key, AndroidNs, value);
            parent.AppendChild(element);
        }
    }
}
