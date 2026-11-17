using EFT;
using EFT.Communications;
using SPT.Reflection.Utils;

namespace Wunderwaffe
{
    /// <summary>
    /// The F12 debug give: asks the server mod (WunderwaffeDebugGive item event) for a loaded Wunderwaffe with three spare
    /// clips, or one clip, straight into the stash. Hideout only: in a raid the stash isn't the live inventory.
    /// </summary>
    internal static class WunderwaffeDebugGive
    {
        private const string Action = "WunderwaffeDebugGive";

        public static void Send(string what)
        {
            if (!(LocalPlayer.Get() is HideoutPlayer))
            {
                Notify("Wunderwaffe DG-2: use the debug give in the hideout.");
                return;
            }
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
            {
                WunderwaffePlugin.Log.LogWarning("Debug give: no backend session");
                return;
            }
            session.SendOperationRightNow(new GiveOperation { Action = Action, item = what }, result =>
            {
                if (result == null || result.Failed)
                {
                    WunderwaffePlugin.Log.LogWarning($"Debug give {what} refused: {result?.Error}");
                    Notify($"Wunderwaffe DG-2: {result?.Error}");
                }
                else
                {
                    WunderwaffePlugin.Log.LogInfo($"Debug give {what}: done");
                    Notify(what == "weapon" ? "Wunderwaffe DG-2 and 3 clips put in your stash." : "Wunderwaffe battery clip put in your stash.");
                }
            });
        }

        private static void Notify(string text)
        {
            try
            {
                NotificationManager.DisplayMessageNotification(text);
            }
            catch
            {
            }
        }

        /// <summary>Item event body; field names match the server's WunderwaffeGiveRequest.</summary>
        private class GiveOperation
        {
            public string Action;
            public string item;
        }
    }
}
