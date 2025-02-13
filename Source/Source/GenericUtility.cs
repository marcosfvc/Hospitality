using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Hospitality
{
    internal static class GenericUtility
    {
        internal const int NoBasesLeft = -1;

        private static Dictionary<Faction, int> travelDaysCache = new Dictionary<Faction, int>();

        public static bool IsMeal(this Thing thing)
        {
            return thing.def.ingestible?.IsMeal == true;
        }

        public static Pawn GetAnyRelatedWorldPawn(Func<Pawn, bool> selector, int minImportance)
        {
            // Get all important relations from all colonists
            var importantRelations = from colonist in PawnsFinder.AllMaps_FreeColonistsSpawned.Where(c => !c.Dead)
                from otherPawn in colonist.relations.RelatedPawns
                where !otherPawn.Dead && !otherPawn.Spawned && otherPawn.Faction != colonist.Faction && selector(otherPawn) && otherPawn.IsWorldPawn()
                select new {otherPawn, colonist, relationDef = colonist.GetMostImportantRelation(otherPawn)};

            var dictRelations = new Dictionary<Pawn, float>();

            // Calculate the total importance to colony
            foreach (var relation in importantRelations.Where(r=>r.relationDef.importance >= minImportance))
            {
                if (!dictRelations.ContainsKey(relation.otherPawn))
                {
                    dictRelations.Add(relation.otherPawn, relation.relationDef.importance);
                }
                else dictRelations[relation.otherPawn] += relation.relationDef.importance;
            }
            //Log.Message(dictRelations.Count + " distinct pawns:");
            //foreach (var relation in dictRelations)
            //{
            //    Log.Message("- " + relation.Key.Name + ": " + relation.Value +(relation.Key.Faction.leader == relation.Key?" (leader)":""));
            //}

            if (dictRelations.Count > 0)
            {
                var choice = dictRelations.RandomElementByWeight(pair => pair.Value);
                //Log.Message(choice.Key.Name + " with " + choice.Value + " points was chosen.");
                return choice.Key;
            }
            else if (minImportance <= 0)
            {
                Log.Message("Couldn't find any pawn that is related to colony.");
                return null;
            }
            else return GetAnyRelatedWorldPawn(selector, minImportance - 50);
        }

        public static float GetTravelDays(Faction faction, Map map)
        {
            if (travelDaysCache.TryGetValue(faction, out var minTicks)) return minTicks / (float)GenDate.TicksPerDay;

            minTicks = Int32.MaxValue;
            foreach (var settlement in Find.WorldObjects.SettlementBases)
            {
                if (settlement.Faction != faction) continue;
                int travelTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, settlement.Tile, null);
                if (travelTicks <= 0) continue;
                if (travelTicks < minTicks) minTicks = travelTicks;
            }
            if (minTicks == Int32.MaxValue) return NoBasesLeft;

            travelDaysCache.Add(faction, minTicks);

            //Log.Message("It takes the " + faction.def.pawnsPlural + " " + days + " days to travel to the player.");
            return minTicks / (float)GenDate.TicksPerDay;
        }

        internal static void CheckTooManyIncidentsAtOnce(IncidentQueue incidentQueue)
        {
            const int maxIncidents = 6;
            const int rangeOfDays = 3;

            if (incidentQueue.Count < maxIncidents) return;
            int index = 0;
            foreach (QueuedIncident incident in incidentQueue)
            {
                index++;
                if (index == maxIncidents)
                {
                    if (incident.FireTick - GenTicks.TicksGame < GenDate.TicksPerDay*rangeOfDays)
                    {
                        Log.Message(String.Format("More than {0} visitor groups planned within the next {1} days. Cancelling half.", maxIncidents - 1, rangeOfDays));
                        RemoveSomeIncidents(incidentQueue);
                    }
                }
            }
        }

        private static void RemoveSomeIncidents(IncidentQueue incidentQueue)
        {
            const int rangeOfDays = 3;
            IncidentQueue backupQueue = new IncidentQueue();

            bool skip = true;
            int amount = incidentQueue.Count;
            
            // Copy and thin 
            foreach (QueuedIncident incident in incidentQueue)
            {
                // After range of days copy everything
                if (incident.FireTick - GenTicks.TicksGame >= GenDate.TicksPerDay*rangeOfDays) backupQueue.Add(incident);
                else
                {
                    // Before, copy every second incident
                    if (!skip) backupQueue.Add(incident);
                    skip = !skip;
                }
            }
            // Add them back
            incidentQueue.Clear();
            foreach (QueuedIncident incident in backupQueue)
            {
                incidentQueue.Add(incident);
            }

            Log.Message($"Reduced {amount} visits to {incidentQueue.Count}, by cancelling every 2nd within the next {rangeOfDays} days.");
        }

        internal static void FillIncidentQueue(Map map)
        {
            // Add some visits
            float days = Rand.Range(10f, 16f); // initial delay
            int amount = Rand.Range(1, 4);
            foreach (var faction in Find.FactionManager.AllFactionsVisible.Where(f => !f.IsPlayer && f != Faction.OfPlayer && !f.HostileTo(Faction.OfPlayer)).OrderBy(f => GetTravelDays(f, map)))
            {
                amount--;
                Log.Message(faction.GetCallLabel() + " are coming after " + days + " days.");
                PlanNewVisit(map, days, faction);
                days += Rand.Range(15f, 25f);
                if (amount <= 0) break;
            }
        }

        public static void PlanNewVisit(IIncidentTarget map, float afterDays, Faction faction = null)
        {
            var realMap = map as Map;
            if (realMap == null) return;

            var incidentParms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.FactionArrival, realMap);

            if (faction == null) return;

            incidentParms.faction = faction;
            var incident = new FiringIncident(IncidentDefOf.VisitorGroup, null, incidentParms);
            Hospitality_MapComponent.Instance(realMap).QueueIncident(incident, afterDays);
        }

        public static void TryCreateVisit(Map map, float days, Faction faction, float travelFactor = 1)
        {
            var travelDays = GetTravelDays(faction, map);

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (travelDays == NoBasesLeft) return;

            travelDays = days + travelDays * travelFactor;
            PlanNewVisit(map, travelDays, faction);
        }

        public static void DoAreaRestriction(Pawn pawn, Rect rect, Area area, Action<Area> setArea, Func<Area, string> getLabel)
        {
            // Needed for GUI
            if (pawn.playerSettings == null)
            {
                pawn.playerSettings = new Pawn_PlayerSettings(pawn) {AreaRestriction = area};
            }

            pawn.playerSettings.AreaRestriction = area;
            GuestUtility.DoAllowedAreaSelectors(rect, pawn, getLabel);
            var newArea = pawn.playerSettings.AreaRestriction;
            pawn.playerSettings.AreaRestriction = null;
            Text.Anchor = TextAnchor.UpperLeft;

            if (newArea != area) setArea(newArea);
        }

        public static string GetShoppingLabel(Area area)
        {
            if (area != null) return area.Label;
            return "AreaNoShopping".Translate();
        }
    }
}