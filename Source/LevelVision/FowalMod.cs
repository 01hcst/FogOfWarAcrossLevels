using UnityEngine;
using Verse;

namespace FogOfWarAcrossLevels
{
    public class FowalSettings : ModSettings
    {
        public const int DefaultUpDepth = 3;
        public const int MaxUpDepth = 8;

        public bool seeDown = true;
        public bool seeUp = true;
        public int upDepth = DefaultUpDepth;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref seeDown, "seeDown", true);
            Scribe_Values.Look(ref seeUp, "seeUp", true);
            Scribe_Values.Look(ref upDepth, "upDepth", DefaultUpDepth);
        }
    }

    public class FowalMod : Mod
    {
        public static FowalSettings Settings;

        public FowalMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FowalSettings>();
        }

        public override string SettingsCategory() => "FOWAL_Title".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("FOWAL_SeeDown".Translate(), ref Settings.seeDown, "FOWAL_SeeDownDesc".Translate());
            list.CheckboxLabeled("FOWAL_SeeUp".Translate(), ref Settings.seeUp, "FOWAL_SeeUpDesc".Translate());
            if (Settings.seeUp)
            {
                list.Label("FOWAL_UpDepth".Translate(Settings.upDepth), tooltip: "FOWAL_UpDepthDesc".Translate());
                Settings.upDepth = Mathf.RoundToInt(list.Slider(Settings.upDepth, 1f, FowalSettings.MaxUpDepth));
            }
            list.End();
        }
    }
}
