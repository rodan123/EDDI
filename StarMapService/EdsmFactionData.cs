﻿using EddiDataDefinitions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Utilities;

namespace EddiStarMapService
{
    public partial class StarMapService
    {
        public List<Faction> GetStarMapFactions(string systemName, long? edsmId = null)
        {
            if (systemName == null) { return null; }


            var request = new RestRequest("api-system-v1/factions", Method.POST);
            request.AddParameter("systemName", systemName);
            request.AddParameter("systemId", edsmId);
            var clientResponse = restClient.Execute<JObject>(request);
            if (clientResponse.IsSuccessful)
            {
                var token = JToken.Parse(clientResponse.Content);
                if (token is JObject response)
                {
                    return ParseStarMapFactions(response, systemName);
                }
            }
            else
            {
                Logging.Debug("EDSM responded with " + clientResponse.ErrorMessage, clientResponse.ErrorException);
            }
            return null;
        }

        public List<Faction> ParseStarMapFactions(JObject response, string systemName)
        {
            List<Faction> Factions = new List<Faction>();
            if (response != null)
            {
                JArray factions = (JArray)response["factions"];

                if (factions != null)
                {
                    foreach (JObject faction in factions)
                    {
                        try
                        {
                            Faction Faction = ParseStarMapFaction(faction, systemName);
                            Factions.Add(Faction);
                        }
                        catch (Exception ex)
                        {
                            Dictionary<string, object> data = new Dictionary<string, object>
                            {
                                {"faction", JsonConvert.SerializeObject(faction)},
                                {"exception", ex.Message},
                                {"stacktrace", ex.StackTrace}
                            };
                            Logging.Error("Error parsing EDSM faction result.", data);
                        }
                    }
                }
            }
            return Factions;
        }

        private Faction ParseStarMapFaction(JObject faction, string systemName)
        {
            if (faction is null) { return null; }
            Faction Faction = new Faction
            {
                name = (string)faction["name"],
                EDSMID = (long?)faction["id"],
                Allegiance = Superpower.FromName((string)faction["allegiance"]) ?? Superpower.None,
                Government = Government.FromName((string)faction["government"]) ?? Government.None,
                isplayer = (bool?)faction["isPlayer"],
                updatedAt = Dates.fromTimestamp((long?)faction["lastUpdate"]) ?? DateTime.MinValue
            };

            Faction.presences.Add(new FactionPresence()
            {
                systemName = systemName,
                influence = (decimal?)faction["influence"] * 100, // Convert from a 0-1 range to a percentage
                FactionState = FactionState.FromName((string)faction["state"]) ?? FactionState.None,
            });

            IDictionary<string, object> factionDetail = faction.ToObject<IDictionary<string, object>>();

            // Active states
            factionDetail.TryGetValue("ActiveStates", out object activeStatesVal);
            if (activeStatesVal != null)
            {
                var activeStatesList = (List<object>)activeStatesVal;
                foreach (IDictionary<string, object> activeState in activeStatesList)
                {
                    Faction.presences.FirstOrDefault(p => p.systemName == systemName)?
                        .ActiveStates.Add(FactionState.FromEDName(JsonParsing.getString(activeState, "State") ?? "None"));
                }
            }

            // Pending states
            factionDetail.TryGetValue("PendingStates", out object pendingStatesVal);
            if (pendingStatesVal != null)
            {
                var pendingStatesList = (List<object>)pendingStatesVal;
                foreach (IDictionary<string, object> pendingState in pendingStatesList)
                {
                    FactionTrendingState pTrendingState = new FactionTrendingState(
                        FactionState.FromEDName(JsonParsing.getString(pendingState, "State") ?? "None"),
                        JsonParsing.getInt(pendingState, "Trend")
                    );
                    Faction.presences.FirstOrDefault(p => p.systemName == systemName)?
                        .PendingStates.Add(pTrendingState);
                }
            }

            // Recovering states
            factionDetail.TryGetValue("RecoveringStates", out object recoveringStatesVal);
            if (recoveringStatesVal != null)
            {
                var recoveringStatesList = (List<object>)recoveringStatesVal;
                foreach (IDictionary<string, object> recoveringState in recoveringStatesList)
                {
                    FactionTrendingState rTrendingState = new FactionTrendingState(
                        FactionState.FromEDName(JsonParsing.getString(recoveringState, "State") ?? "None"),
                        JsonParsing.getInt(recoveringState, "Trend")
                    );
                    Faction.presences.FirstOrDefault(p => p.systemName == systemName)?
                        .RecoveringStates.Add(rTrendingState);
                }
            }

            return Faction;
        }
    }
}
