﻿using Newtonsoft.Json.Linq;
using NLog;

namespace MultiFunPlayer.Settings.Migrations;

internal sealed class Migration0028 : AbstractConfigMigration
{
    protected override Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override void Migrate(JObject settings)
    {
        RemovePropertiesByPaths(settings, [
            "$.Shortcut.IsKeyboardKeysGestureEnabled",
            "$.Shortcut.IsMouseAxisGestureEnabled",
            "$.Shortcut.IsMouseButtonGestureEnabled",
            "$.Shortcut.IsGamepadAxisGestureEnabled",
            "$.Shortcut.IsGamepadButtonGestureEnabled",
            "$.Shortcut.IsTCodeButtonGestureEnabled",
            "$.Shortcut.IsTCodeAxisGestureEnabled"
        ], selectMultiple: false);

        base.Migrate(settings);
    }
}