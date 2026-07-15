using DynamicMaps.Config;
using EFT;
using UnityEngine;

namespace DynamicMaps.Utils
{
    /// <summary>
    /// Detects whether an AI bot has the main player on its EFT enemy/threat list.
    /// Native EnemiesController path — no SAIN hard dependency.
    /// </summary>
    public static class BotAgroUtils
    {
        public static bool IsBotAgroOnMainPlayer(IPlayer botPlayer)
        {
            if (!Settings.ShowBotAgroFlashInRaid.Value)
            {
                return false;
            }

            if (botPlayer == null || botPlayer.IsYourPlayer)
            {
                return false;
            }

            var main = GameUtils.GetMainPlayer();
            if (main == null)
            {
                return false;
            }

            // Teammates should not flash as agro.
            if (botPlayer is Player p && p.IsGroupedWithMainPlayer())
            {
                return false;
            }

            var botOwner = botPlayer.AIData?.BotOwner;
            if (botOwner?.EnemiesController?.EnemyInfos == null)
            {
                return false;
            }

            var infos = botOwner.EnemiesController.EnemyInfos;
            if (infos.Count == 0)
            {
                return false;
            }

            var mainId = main.ProfileId;
            foreach (var pair in infos)
            {
                // Key is typically IPlayer; Value is EnemyInfo (ProfileId on either).
                if (pair.Key != null && pair.Key.ProfileId == mainId)
                {
                    return true;
                }

                if (pair.Value != null && pair.Value.ProfileId == mainId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
