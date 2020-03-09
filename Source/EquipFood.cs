using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using Verse.AI;
using RimWorld;
using Harmony;
using System.Reflection;
using UnityEngine;

namespace EquipFood
{
    public class EquipFoodSettings : ModSettings
    {
        private static bool _makeExceptionForPackedLunch = false;
        public static bool makeExceptionForPackedLunch { get { return _makeExceptionForPackedLunch; } }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var controls = new Listing_Standard();
            controls.Begin(inRect);
            controls.CheckboxLabeled(
                "Colonists disallowed from equipping food may still equip \"packed lunch\"-type food",
                ref _makeExceptionForPackedLunch);
            controls.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref _makeExceptionForPackedLunch, "PackedLunchException", false);
        }
    }

    public class Mod : Verse.Mod
    {
        public Mod(ModContentPack content) : base(content)
        {
            GetSettings<EquipFoodSettings>();
            var harmony = HarmonyInstance.Create("likeafox.rimworld.equipfood");
            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
        }

        public override string SettingsCategory() => "EquipFood";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            EquipFoodSettings.DoSettingsWindowContents(inRect);
        }
    }

    public class EquipFood : GameComponent
    {
        public HashSet<Pawn> foodPackingAbstainers = new HashSet<Pawn>();

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
                foodPackingAbstainers.RemoveWhere(p => p.Destroyed);
            Scribe_Collections.Look(ref foodPackingAbstainers, "FoodPackingAbstainers", LookMode.Reference);
        }

        public static EquipFood instance { get; private set; }
        public EquipFood(Game game) : this() {
        }
        public EquipFood() { instance = this; }

        private static JobGiver_PackFood _packfood;

        public static T PackFood<T>(string fn, Pawn pawn, Thing thing = null)
            where T : struct
        {
            if (_packfood == null) _packfood = new JobGiver_PackFood();
            MethodInfo m = typeof(JobGiver_PackFood).GetMethod(fn,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m.ReturnType != typeof(T))
                throw new ArgumentException();
            var args = m.GetParameters()
                .Select<ParameterInfo, object>(delegate (ParameterInfo p)
            {
                var pT = p.ParameterType;
                if (pT == typeof(Pawn)) return pawn;
                else if (pT == typeof(Thing)) return thing;
                else throw new ArgumentException("type now allowed");
            }).ToArray();
            return (T)m.Invoke(_packfood, args);
        }

        public static int CountFoodEquippable(Pawn pawn, Thing food)
        {
            //can pawn get it?
            if (!PackFood<bool>("IsGoodPackableFoodFor", pawn, food)
                || food.IsForbidden(pawn)
                || !Verse.AI.ReservationUtility.CanReserve(pawn, food)
                || !food.IsSociallyProper(pawn)
                || !pawn.CanReach(food, PathEndMode.ClosestTouch, Danger.Deadly))
                return 0;
            //how many can pawn get?
            float invNutrition = PackFood<float>("GetInventoryPackableFoodNutrition", pawn);
            float foodNutritionPer = food.GetStatValue(StatDefOf.Nutrition);
            int foodsBeforeSatiety = (int)Math.Floor((pawn.needs.food.MaxLevel - invNutrition) / foodNutritionPer);
            return Math.Max(Math.Min(foodsBeforeSatiety, food.stackCount), (invNutrition == 0f) ? 1 : 0);
        }

        public static FloatMenuOption EquipFoodOption(IntVec3 clickCell, Pawn pawn)
        {
            Thing food = clickCell.GetFirstItem(pawn.Map);
            if (food == null)
                return null;
            int getcount = CountFoodEquippable(pawn, food);
            if (getcount == 0)
                return null;

            Job job = new Job(JobDefOf.TakeInventory, food) { count = getcount };

            Action action = delegate
            {
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                {
                    MoteMaker.MakeStaticMote(clickCell, pawn.Map, ThingDefOf.Mote_FeedbackEquip, 1f);
                }
            };
            return new FloatMenuOption("Equip "+food.def.label, action, MenuOptionPriority.Low);
        }

        public static bool CanMakeExceptionFor(Thing food)
        {
            return (EquipFoodSettings.makeExceptionForPackedLunch
                 && food.def.IsNutritionGivingIngestible
                 && food.def.ingestible.chairSearchRadius <= 10f);
        }
    }

    public static class Extensions
    {
        public static bool EquipsFood(this Pawn p)
        {
            return !EquipFood.instance.foodPackingAbstainers.Contains(p);
        }

        public static void SetEquipFood(this Pawn p, bool v)
        {
            if (v)
                EquipFood.instance.foodPackingAbstainers.Remove(p);
            else
                EquipFood.instance.foodPackingAbstainers.Add(p);
        }
    }

    public class PawnColumnWorker_EquipFood : PawnColumnWorker_Checkbox
    {
        protected override bool GetValue(Pawn pawn)
        {
            return pawn.EquipsFood();
        }

        protected override void SetValue(Pawn pawn, bool value)
        {
            pawn.SetEquipFood(value);
        }
    }

    [StaticConstructorOnStartup]
    public static class OnStartup
    {
        static OnStartup()
        {
            try
            {
                //reorder PawnTableDefOf.Assign.columns
                List<PawnColumnDef> cols = PawnTableDefOf.Assign.columns;
                int food_res_ind = cols.FindIndex(c => c.defName == "FoodRestriction");
                int equip_res_ind = cols.FindIndex(c => c.defName == "EquipFood");
                PawnColumnDef equipFood = cols[equip_res_ind];
                cols.RemoveAt(equip_res_ind);
                cols.Insert(food_res_ind + 1, equipFood);
            }
            catch { Log.Warning("Unable to reorder PawnTableDefOf.Assign.columns"); }
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "ChoicesAtFor")]
    class FloatMenuMakerMap_ChoicesAtFor_Patch
    {
        static void Postfix(ref List<FloatMenuOption> __result, UnityEngine.Vector3 clickPos, Pawn pawn)
        {
            IntVec3 intVec = IntVec3.FromVector3(clickPos);
            var op = EquipFood.EquipFoodOption(intVec, pawn);
            if (op != null)
                __result.Add(op);
        }
    }

    [HarmonyPatch(typeof(JobGiver_PackFood), "IsGoodPackableFoodFor")]
    class JobGiver_PackFood_IsGoodPackableFoodFor_Patch
    {
        static void Postfix(ref bool __result, Thing food, Pawn forPawn)
        {
            if (!forPawn.EquipsFood() && !EquipFood.CanMakeExceptionFor(food))
                __result = false;
        }
    }
}
