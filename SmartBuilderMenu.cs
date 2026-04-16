using UnityEngine;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("SmartBuilderMenu", "TheVekT", "1.0.1")]
    [Description("Удобное меню управления BGrade и скинами на колесико мыши")]
    class SmartBuilderMenu : RustPlugin
    {
        private const string UIName = "SmartBuilderUI";
        private const string PermUse = "smartbuildermenu.use";

        // Список игроков(их userID), у которых открыто меню
        private HashSet<ulong> openUIs = new HashSet<ulong>();
        
        // НОВОЕ: Словарь для хранения текущего выбранного уровня грейда игрока
        private Dictionary<ulong, int> playerGrades = new Dictionary<ulong, int>();

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
        }

        // НОВОЕ: Очистка памяти, если игрок вышел с сервера
        private void OnPlayerDisconnected(BasePlayer player)
        {
            openUIs.Remove(player.userID);
            playerGrades.Remove(player.userID);
        }

        // НОВОЕ: Перехватываем ручной ввод в чат. Если игрок напишет /bgrade 2, меню обновит свой статус.
        private void OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (command.ToLower() == "bgrade" && args.Length > 0)
            {
                if (int.TryParse(args[0], out int level))
                {
                    playerGrades[player.userID] = level;
                }
            }
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null) return;
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse)) return;

            if (input.WasJustPressed(BUTTON.FIRE_THIRD))
            {
                if (openUIs.Contains(player.userID))
                {
                    CloseMenu(player);
                }
                else 
                {
                    var activeItem = player.GetActiveItem();
                    if (activeItem != null && (activeItem.info.shortname == "hammer" || activeItem.info.shortname == "building.planner"))
                    {
                        OpenMenu(player);
                    }
                }
            }
        }

        private void CloseMenu(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UIName);
            openUIs.Remove(player.userID);
        }

        private void OpenMenu(BasePlayer player)
        {
            CloseMenu(player);
            openUIs.Add(player.userID);

            // НОВОЕ: Узнаем текущий грейд (по умолчанию 0 - Twig/Выкл)
            int currentGrade = 0;
            if (playerGrades.ContainsKey(player.userID))
            {
                currentGrade = playerGrades[player.userID];
            }

            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-200 -150", OffsetMax = "200 150" },
                CursorEnabled = true
            }, "Overlay", UIName);

            elements.Add(new CuiLabel
            {
                Text = { Text = "SMART BUILDER MENU", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0 0.85", AnchorMax = "1 1" }
            }, UIName);

            elements.Add(new CuiButton
            {
                Button = { Command = "smartmenu_cmd close", Color = "0.8 0.2 0.2 0.8" },
                RectTransform = { AnchorMin = "0.9 0.85", AnchorMax = "1 1" },
                Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UIName);

            // --- КНОПКИ УПРАВЛЕНИЯ BGRADE ---
            // НОВОЕ: Передаем проверку "currentGrade == X", чтобы кнопка знала, активна ли она
            CreateButton(elements, UIName, "Twig", "smartmenu_cmd bgrade_0", "0.05 0.55", "0.45 0.75", "0.4 0.4 0.4 0.8", currentGrade == 0);
            CreateButton(elements, UIName, "Wood", "smartmenu_cmd bgrade_1", "0.55 0.55", "0.95 0.75", "0.55 0.35 0.15 0.8", currentGrade == 1);
            CreateButton(elements, UIName, "Stone", "smartmenu_cmd bgrade_2", "0.05 0.30", "0.45 0.50", "0.6 0.6 0.6 0.8", currentGrade == 2);
            CreateButton(elements, UIName, "Metal", "smartmenu_cmd bgrade_3", "0.55 0.30", "0.95 0.50", "0.7 0.3 0.2 0.8", currentGrade == 3);
            CreateButton(elements, UIName, "HQM", "smartmenu_cmd bgrade_4", "0.05 0.05", "0.45 0.25", "0.2 0.6 0.7 0.8", currentGrade == 4);

            // --- КНОПКА СКИНОВ ---
            CreateButton(elements, UIName, "Building Skins", "smartmenu_cmd bskin", "0.55 0.05", "0.95 0.25", "0.8 0.5 0.1 0.8", false);

            CuiHelper.AddUi(player, elements);
        }

        // НОВОЕ: Добавлен параметр isActive
        private void CreateButton(CuiElementContainer container, string parent, string text, string command, string anchorMin, string anchorMax, string color, bool isActive)
        {
            // Если кнопка активна: делаем текст жирным, добавляем снизу яркую надпись "ВЫБРАН" и убираем прозрачность кнопки (0.8 -> 1.0)
            string displayText = isActive ? $"<b>{text}</b>\n<size=11><color=#7FFF00>Selected</color></size>" : text;
            string btnColor = isActive ? color.Replace("0.8", "1.0") : color;
            string textColor = isActive ? "1 1 1 1" : "0.7 0.7 0.7 1"; // Неактивный текст чуть темнее

            container.Add(new CuiButton
            {
                Button = { Command = command, Color = btnColor },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Text = { Text = displayText, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent);
        }

        [ConsoleCommand("smartmenu_cmd")]
        private void CmdMenuAction(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse)) return;

            string action = arg.Args[0];

            CloseMenu(player);

            if (action == "close") return;

            if (action.StartsWith("bgrade_"))
            {
                string levelStr = action.Split('_')[1];
                if (int.TryParse(levelStr, out int level))
                {
                    // НОВОЕ: Запоминаем выбор игрока в наш словарь перед отправкой команды
                    playerGrades[player.userID] = level;
                    player.SendConsoleCommand($"chat.say \"/bgrade {level}\"");
                }
            }
            else if (action == "bskin")
            {
                player.SendConsoleCommand("chat.say \"/bskin\"");
            }
        }
    }
}