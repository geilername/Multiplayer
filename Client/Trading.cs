﻿using Harmony;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Multiplayer.Client
{
    public class MpTradeSession : IExposable, ISessionWithTransferables
    {
        public static MpTradeSession current;

        public int sessionId;
        public ITrader trader;
        public Pawn playerNegotiator;
        public bool giftMode;
        public MpTradeDeal deal;
        public bool giftsOnly;

        public string Label
        {
            get
            {
                if (trader is Pawn pawn)
                    return pawn.Faction.Name;
                return trader.TraderName;
            }
        }

        public int SessionId => sessionId;

        public MpTradeSession() { }

        private MpTradeSession(ITrader trader, Pawn playerNegotiator, bool giftMode)
        {
            sessionId = Multiplayer.GlobalIdBlock.NextId();

            this.trader = trader;
            this.playerNegotiator = playerNegotiator;
            this.giftMode = giftMode;
            giftsOnly = giftMode;
        }

        public static void TryCreate(ITrader trader, Pawn playerNegotiator, bool giftMode)
        {
            // todo show error messages?
            if (Multiplayer.WorldComp.trading.Any(s => s.trader == trader))
                return;

            if (Multiplayer.WorldComp.trading.Any(s => s.playerNegotiator == playerNegotiator))
                return;

            MpTradeSession session = new MpTradeSession(trader, playerNegotiator, giftMode);
            Multiplayer.WorldComp.trading.Add(session);

            CancelTradeDealReset.cancel = true;
            SetTradeSession(session, true);

            try
            {
                session.deal = new MpTradeDeal(session);

                Thing permSilver = ThingMaker.MakeThing(ThingDefOf.Silver, null);
                permSilver.stackCount = 0;
                session.deal.permanentSilver = permSilver;
                session.deal.AddToTradeables(permSilver, Transactor.Trader);

                session.deal.AddAllTradeables();
                session.StartWaitingJobs();
            }
            finally
            {
                SetTradeSession(null);
                CancelTradeDealReset.cancel = false;
            }
        }

        private void StartWaitingJobs()
        {
            if (playerNegotiator.Spawned && trader is Pawn traderPawn && traderPawn.Spawned)
            {
                playerNegotiator.jobs.StartJob(new Job(JobDefOf.Wait, 10, true) { count = 1234, targetA = traderPawn }, JobCondition.InterruptForced);
                traderPawn.jobs.StartJob(new Job(JobDefOf.Wait, 10, true) { count = 1234, targetA = playerNegotiator }, JobCondition.InterruptForced);
            }
        }

        public bool ShouldCancel()
        {
            if (!trader.CanTradeNow)
                return true;

            if (playerNegotiator.Drafted)
                return true;

            if (trader is Pawn pawn && pawn.Spawned && playerNegotiator.Spawned)
                return pawn.Position.DistanceToSquared(playerNegotiator.Position) > 2 * 2;

            return false;
        }

        public void TryExecute()
        {
            deal.recacheColony = true;
            deal.recacheTrader = true;
            deal.Recache();

            SetTradeSession(this);
            bool executed = deal.TryExecute(out bool traded);
            SetTradeSession(null);

            if (executed)
                Multiplayer.WorldComp.RemoveTradeSession(this);
        }

        public void Reset()
        {
            deal.tradeables.ForEach(t => t.countToTransfer = 0);
            deal.uiShouldReset = UIShouldReset.Silent;
        }

        public void ToggleGiftMode()
        {
            giftMode = !giftMode;
            deal.tradeables.ForEach(t => t.countToTransfer = 0);
            deal.uiShouldReset = UIShouldReset.Silent;
        }

        public static void SetTradeSession(MpTradeSession session, bool force = false)
        {
            if (!force && TradeSession.deal == session?.deal) return;

            current = session;
            TradeSession.trader = session?.trader;
            TradeSession.playerNegotiator = session?.playerNegotiator;
            TradeSession.giftMode = session?.giftMode ?? false;
            TradeSession.deal = session?.deal;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sessionId, "sessionId");

            ILoadReferenceable trader = (ILoadReferenceable)this.trader;
            Scribe_References.Look(ref trader, "trader");
            this.trader = (ITrader)trader;

            Scribe_References.Look(ref playerNegotiator, "playerNegotiator");
            Scribe_Values.Look(ref giftMode, "giftMode");
            Scribe_Values.Look(ref giftsOnly, "giftsOnly");

            Scribe_Deep.Look(ref deal, "tradeDeal", this);
        }

        public Transferable GetTransferableByThingId(int thingId)
        {
            for (int i = 0; i < deal.tradeables.Count; i++)
            {
                Tradeable tr = deal.tradeables[i];
                if (tr.FirstThingColony?.thingIDNumber == thingId)
                    return tr;
                if (tr.FirstThingTrader?.thingIDNumber == thingId)
                    return tr;
            }

            return null;
        }

        public void Notify_CountChanged(Transferable tr)
        {
            deal.caravanDirty = true;
        }
    }

    public class MpTradeDeal : TradeDeal, IExposable
    {
        public MpTradeSession session;

        private static HashSet<Thing> newThings = new HashSet<Thing>();
        private static HashSet<Thing> oldThings = new HashSet<Thing>();

        public UIShouldReset uiShouldReset;

        public HashSet<Thing> recacheThings = new HashSet<Thing>();
        public bool recacheColony;
        public bool recacheTrader;
        public bool ShouldRecache => recacheColony || recacheTrader || recacheThings.Count > 0;
        public bool caravanDirty;

        public Thing permanentSilver;

        public MpTradeDeal(MpTradeSession session)
        {
            this.session = session;
        }

        public void Recache()
        {
            if (recacheColony)
                CheckAddRemoveColony();

            if (recacheTrader)
                CheckAddRemoveTrader();

            if (recacheThings.Count > 0)
                CheckReassign();

            uiShouldReset = UIShouldReset.Full;
            recacheThings.Clear();
            recacheColony = false;
            recacheTrader = false;
        }

        private void CheckAddRemoveColony()
        {
            foreach (Thing t in session.trader.ColonyThingsWillingToBuy(session.playerNegotiator))
                newThings.Add(t);

            for (int i = tradeables.Count - 1; i >= 0; i--)
            {
                Tradeable tradeable = tradeables[i];
                int toRemove = 0;

                for (int j = tradeable.thingsColony.Count - 1; j >= 0; j--)
                {
                    Thing thingColony = tradeable.thingsColony[j];
                    if (!newThings.Contains(thingColony))
                        toRemove++;
                    else
                        oldThings.Add(thingColony);
                }

                if (toRemove == 0) continue;

                if (toRemove == tradeable.thingsColony.Count + tradeable.thingsTrader.Count)
                    tradeables.RemoveAt(i);
                else
                    tradeable.thingsColony.RemoveAll(t => !newThings.Contains(t));
            }

            foreach (Thing newThing in newThings)
                if (!oldThings.Contains(newThing))
                    AddToTradeables(newThing, Transactor.Colony);

            newThings.Clear();
            oldThings.Clear();
        }

        private void CheckAddRemoveTrader()
        {
            newThings.Add(permanentSilver);

            foreach (Thing t in session.trader.Goods)
                newThings.Add(t);

            for (int i = tradeables.Count - 1; i >= 0; i--)
            {
                Tradeable tradeable = tradeables[i];
                int toRemove = 0;

                for (int j = tradeable.thingsTrader.Count - 1; j >= 0; j--)
                {
                    Thing thingTrader = tradeable.thingsTrader[j];
                    if (!newThings.Contains(thingTrader))
                        toRemove++;
                    else
                        oldThings.Add(thingTrader);
                }

                if (toRemove == 0) continue;

                if (toRemove == tradeable.thingsColony.Count + tradeable.thingsTrader.Count)
                    tradeables.RemoveAt(i);
                else
                    tradeable.thingsTrader.RemoveAll(t => !newThings.Contains(t));
            }

            foreach (Thing newThing in newThings)
                if (!oldThings.Contains(newThing))
                    AddToTradeables(newThing, Transactor.Trader);

            newThings.Clear();
            oldThings.Clear();
        }

        private void CheckReassign()
        {
            for (int i = tradeables.Count - 1; i >= 0; i--)
            {
                Tradeable tradeable = tradeables[i];

                CheckReassign(tradeable, Transactor.Colony);
                CheckReassign(tradeable, Transactor.Trader);

                if (recacheThings.Count == 0) break;
            }
        }

        private void CheckReassign(Tradeable tradeable, Transactor side)
        {
            List<Thing> things = side == Transactor.Colony ? tradeable.thingsColony : tradeable.thingsTrader;

            for (int j = things.Count - 1; j >= 1; j--)
            {
                Thing thing = things[j];
                TransferAsOneMode mode = tradeable.TraderWillTrade ? TransferAsOneMode.Normal : TransferAsOneMode.InactiveTradeable;

                if (recacheThings.Contains(thing))
                {
                    if (!TransferableUtility.TransferAsOne(tradeable.AnyThing, thing, mode))
                        things.RemoveAt(j);
                    else
                        AddToTradeables(thing, side);
                }
            }
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref permanentSilver, "permanentSilver");
            Scribe_Collections.Look(ref tradeables, "tradeables", LookMode.Deep);
        }
    }

    public enum UIShouldReset
    {
        None,
        Silent,
        Full
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.Reset))]
    static class CancelTradeDealReset
    {
        public static bool cancel;

        static bool Prefix() => !cancel && Scribe.mode != LoadSaveMode.LoadingVars;
    }

    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    static class CancelDialogTrade
    {
        static bool Prefix(Window window)
        {
            if (window is Dialog_Trade && (Multiplayer.ExecutingCmds || Multiplayer.Ticking))
                return false;

            return true;
        }
    }

    [HarmonyPatch(typeof(Dialog_Trade), MethodType.Constructor)]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(ITrader), typeof(bool) })]
    static class CancelDialogTradeCtor
    {
        public static bool cancel;

        static bool Prefix(Pawn playerNegotiator, ITrader trader, bool giftsOnly)
        {
            if (cancel) return false;

            if (Multiplayer.ExecutingCmds || Multiplayer.Ticking)
            {
                MpTradeSession.TryCreate(trader, playerNegotiator, giftsOnly);

                if (TickPatch.currentExecutingCmdIssuedBySelf)
                    Find.WindowStack.Add(new TradingWindow());

                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wait), nameof(JobDriver_Wait.DecorateWaitToil))]
    static class TradingWaitJobToil
    {
        static void Postfix(Toil wait)
        {
            wait.AddPreTickAction(() =>
            {
                Job job = wait.actor.CurJob;
                if (job.count == 1234 && Multiplayer.WorldComp.trading.Any(s => s.playerNegotiator == wait.actor || s.trader == wait.actor))
                {
                    job.startTick = Find.TickManager.TicksGame + 1; // Don't expire while trading
                }
            });
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wait), nameof(JobDriver_Wait.GetReport))]
    static class TradingWaitJobReport
    {
        static void Postfix(JobDriver_Wait __instance, ref string __result)
        {
            if (__instance.job.count == 1234)
                __result = "Negotiating trade";
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_TraderCaravanArrival), nameof(IncidentWorker_TraderCaravanArrival.TryExecuteWorker))]
    static class ArriveAtCenter
    {
        static void Prefix(IncidentParms parms)
        {
            parms.spawnCenter = (parms.target as Map).Center;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    static class NullCheckDialogTrade
    {
        static IEnumerable<CodeInstruction> Transpiler(ILGenerator gen, IEnumerable<CodeInstruction> e)
        {
            List<CodeInstruction> insts = new List<CodeInstruction>(e);
            LocalBuilder local = gen.DeclareLocal(typeof(Dialog_Trade));

            for (int i = 0; i < insts.Count; i++)
            {
                CodeInstruction inst = insts[i];
                yield return inst;

                if (inst.opcode == OpCodes.Callvirt && ((MethodInfo)inst.operand).Name == nameof(WindowStack.WindowOfType))
                {
                    Label label = gen.DefineLabel();
                    insts[i + 2].labels.Add(label);

                    yield return new CodeInstruction(OpCodes.Stloc, local);
                    yield return new CodeInstruction(OpCodes.Ldloc, local);
                    yield return new CodeInstruction(OpCodes.Brfalse, label);
                    yield return new CodeInstruction(OpCodes.Ldloc, local);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Reachability), nameof(Reachability.ClearCache))]
    static class ReachabilityChanged
    {
        static void Postfix(Reachability __instance)
        {
            if (Multiplayer.Client != null)
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.map);
        }
    }

    [HarmonyPatch(typeof(Area_Home), nameof(Area_Home.Set))]
    static class AreaHomeChanged
    {
        static void Postfix(Area_Home __instance)
        {
            if (Multiplayer.Client != null)
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.Map);
        }
    }

    [MpPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.AddHaulDestination))]
    [MpPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.RemoveHaulDestination))]
    [MpPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.SetCellFor))]
    [MpPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.ClearCellFor))]
    static class HaulDestinationChanged
    {
        static void Postfix(HaulDestinationManager __instance)
        {
            if (Multiplayer.Client != null)
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.map);
        }
    }

    [HarmonyPatch(typeof(CompRottable), nameof(CompRottable.StageChanged))]
    static class RottableStageChanged
    {
        static void Postfix(CompRottable __instance)
        {
            if (Multiplayer.Client == null) return;
            Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.parent.Map);
        }
    }

    [MpPatch(typeof(ListerThings), nameof(ListerThings.Add))]
    [MpPatch(typeof(ListerThings), nameof(ListerThings.Remove))]
    static class ListerThingsChangedItem
    {
        static void Postfix(ListerThings __instance, Thing t)
        {
            if (Multiplayer.Client == null) return;
            if (t.def.category == ThingCategory.Item && ListerThings.EverListable(t.def, __instance.use))
                Multiplayer.WorldComp.DirtyColonyTradeForMap(t.Map);
        }
    }

    [MpPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.MakeDowned))]
    [MpPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.MakeUndowned))]
    static class PawnDownedStateChanged
    {
        static void Postfix(Pawn_HealthTracker __instance)
        {
            if (Multiplayer.Client != null)
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.pawn.Map);
        }
    }

    [HarmonyPatch(typeof(CompPowerTrader))]
    [HarmonyPatch(nameof(CompPowerTrader.PowerOn), MethodType.Setter)]
    static class OrbitalTradeBeaconPowerChanged
    {
        static void Postfix(CompPowerTrader __instance, bool value)
        {
            if (Multiplayer.Client == null) return;
            if (!(__instance.parent is Building_OrbitalTradeBeacon)) return;
            if (value == __instance.powerOnInt) return;
            if (!Multiplayer.WorldComp.trading.Any(t => t.trader is TradeShip)) return;

            // For trade ships
            Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.parent.Map);
        }
    }

    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch(nameof(Thing.HitPoints), MethodType.Setter)]
    static class ThingHitPointsChanged
    {
        static void Prefix(Thing __instance, int value, ref bool __state)
        {
            if (Multiplayer.Client == null) return;
            __state = __instance.def.category == ThingCategory.Item && value != __instance.hitPointsInt;
        }

        static void Postfix(Thing __instance, bool __state)
        {
            if (__state)
                Multiplayer.WorldComp.DirtyTradeForSpawnedThing(__instance);
        }
    }

    [MpPatch(typeof(ThingOwner), nameof(ThingOwner.NotifyAdded))]
    [MpPatch(typeof(ThingOwner), nameof(ThingOwner.NotifyAddedAndMergedWith))]
    [MpPatch(typeof(ThingOwner), nameof(ThingOwner.NotifyRemoved))]
    static class ThingOwner_ChangedPatch
    {
        static void Postfix(ThingOwner __instance)
        {
            if (Multiplayer.Client == null) return;

            if (__instance.owner is Pawn_InventoryTracker inv)
            {
                ITrader trader = null;

                if (inv.pawn.GetLord()?.LordJob is LordJob_TradeWithColony lordJob)
                    // Carrier inventory changed
                    trader = lordJob.lord.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Trader);
                else if (inv.pawn.trader != null)
                    // Trader inventory changed
                    trader = inv.pawn;

                if (trader != null)
                    Multiplayer.WorldComp.DirtyTraderTradeForTrader(trader);
            }
            else if (__instance.owner is SettlementBase_TraderTracker trader)
            {
                Multiplayer.WorldComp.DirtyTraderTradeForTrader(trader.settlement);
            }
            else if (__instance.owner is TradeShip ship)
            {
                Multiplayer.WorldComp.DirtyTraderTradeForTrader(ship);
            }
        }
    }

    [MpPatch(typeof(Lord), nameof(Lord.AddPawn))]
    [MpPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    static class Lord_TradeChanged
    {
        static void Postfix(Lord __instance)
        {
            if (Multiplayer.Client == null) return;

            if (__instance.LordJob is LordJob_TradeWithColony)
            {
                // Chattel changed
                ITrader trader = __instance.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Trader);
                Multiplayer.WorldComp.DirtyTraderTradeForTrader(trader);
            }
            else if (__instance.LordJob is LordJob_PrisonBreak)
            {
                // Prisoners in a break can't be sold
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.Map);
            }
        }
    }

    [MpPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    [MpPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.ClearMentalStateDirect))]
    static class MentalStateChanged
    {
        static void Postfix(MentalStateHandler __instance)
        {
            if (Multiplayer.Client == null) return;

            // Pawns in a mental state can't be sold
            Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.pawn.Map);
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Notify_Starting))]
    static class JobExitMapStarted
    {
        static void Postfix(JobDriver __instance)
        {
            if (Multiplayer.Client == null) return;

            if (__instance.job.exitMapOnArrival)
            {
                // Prisoners exiting the map can't be sold
                Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.pawn.Map);
            }
        }
    }

    [HarmonyPatch(typeof(SettlementBase_TraderTracker), nameof(SettlementBase_TraderTracker.TraderTrackerTick))]
    static class DontDestroyStockWhileTrading
    {
        static bool Prefix(SettlementBase_TraderTracker __instance)
        {
            return Multiplayer.Client == null || !Multiplayer.WorldComp.trading.Any(t => t.trader == __instance.settlement);
        }
    }

    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.DoListChangedNotifications))]
    static class MapPawnsChanged
    {
        static void Postfix(MapPawns __instance)
        {
            if (Multiplayer.Client == null) return;
            Multiplayer.WorldComp.DirtyColonyTradeForMap(__instance.map);
        }
    }

    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.RecalculateLifeStageIndex))]
    static class PawnLifeStageChanged
    {
        static void Postfix(Pawn_AgeTracker __instance)
        {
            if (Multiplayer.Client == null) return;
            if (!__instance.pawn.Spawned) return;

            Multiplayer.WorldComp.DirtyTradeForSpawnedThing(__instance.pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeTick))]
    static class PawnAgeChanged
    {
        static void Prefix(Pawn_AgeTracker __instance, ref int __state)
        {
            __state = __instance.AgeBiologicalYears;
        }
        
        static void Postfix(Pawn_AgeTracker __instance, int __state)
        {
            if (Multiplayer.Client == null) return;
            if (__state == __instance.AgeBiologicalYears) return;
        }
    }

    [HarmonyPatch(typeof(TransferableUtility), nameof(TransferableUtility.TransferAsOne))]
    static class TransferAsOneAgeCheck_Patch
    {
        static MethodInfo AgeBiologicalFloat = AccessTools.Method(typeof(Pawn_AgeTracker), "get_AgeBiologicalYearsFloat");
        static MethodInfo AgeBiologicalInt = AccessTools.Method(typeof(Pawn_AgeTracker), "get_AgeBiologicalYears");

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand == AgeBiologicalFloat)
                {
                    yield return new CodeInstruction(OpCodes.Callvirt, AgeBiologicalInt);
                    yield return new CodeInstruction(OpCodes.Conv_R4);
                    continue;
                }

                yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.InSellablePosition))]
    static class InSellablePositionPatch
    {
        // todo actually handle this
        static void Postfix(Thing t, ref bool __result, ref string reason)
        {
            if (Multiplayer.Client == null) return;

            __result = t.Spawned;
            reason = null;
        }
    }
}