using DynamicMaps.Config;
using EFT;
using UnityEngine;

namespace DynamicMaps.Utils
{
    /// <summary>
    /// Detects whether an AI bot is actively agroed on the main player.
    /// Uses native EnemiesController — no SAIN hard dependency.
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

            if (!TryGetEnemyInfo(infos, main, out var info) || info == null)
            {
                return false;
            }

            // EnemyInfos can list faction hostiles from raid start — only flash once
            // the bot has actually sensed / engaged the player.
            if (info.HaveSeen || info.IsVisible || info.CanShoot || info.PersonalShoot > 0)
            {
                return true;
            }

            // Current primary target (may be stale under SAIN — still a useful signal).
            var goal = botOwner.Memory?.GoalEnemy;
            if (goal != null && IsSamePlayer(goal, main))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetEnemyInfo(
            System.Collections.Generic.Dictionary<IPlayer, EnemyInfo> infos,
            IPlayer main,
            out EnemyInfo info)
        {
            if (infos.TryGetValue(main, out info) && info != null)
            {
                return true;
            }

            var mainId = main.ProfileId;
            foreach (var pair in infos)
            {
                if (pair.Key != null && pair.Key.ProfileId == mainId)
                {
                    info = pair.Value;
                    return info != null;
                }

                if (pair.Value != null && IsSamePlayer(pair.Value, main))
                {
                    info = pair.Value;
                    return true;
                }
            }

            info = null;
            return false;
        }

        private static bool IsSamePlayer(EnemyInfo enemy, IPlayer main)
        {
            if (enemy == null || main == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(enemy.ProfileId) && enemy.ProfileId == main.ProfileId)
            {
                return true;
            }

            return enemy.Person != null && enemy.Person.ProfileId == main.ProfileId;
        }
    }
}
