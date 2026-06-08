using UnityEngine;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("SmartBuilderMenu", "TheVekT", "1.1.1")]
    [Description("Удобное меню управления BGrade и будівлями CopyPaste на весь экран")]
    class SmartBuilderMenu : RustPlugin
    {
        private const string UIName = "SmartBuilderUI";
        private const string PermUse = "smartbuildermenu.use";

        // Colors
        private const string ColorBackground = "0.08 0.08 0.08 0.7";
        private const string ColorSidebar = "0.05 0.05 0.05 0.98";
        private const string ColorContent = "0.1 0.1 0.1 0.85";
        private const string ColorCard = "0.15 0.15 0.15 0.8";
        private const string ColorTextGreen = "0.47 0.85 0.35 1.0";
        private const string ColorGreenBtn = "0.3 0.65 0.3 0.9";
        private const string ColorBlueBtn = "0.25 0.5 0.7 0.9";
        private const string ColorRedBtn = "0.8 0.25 0.25 0.9";
        private const string ColorGreyBtn = "0.3 0.3 0.3 0.9";
        private const string ColorBtnActive = "0.47 0.85 0.35 0.8";
        private const string ColorSidebarBtnActive = "0.22 0.22 0.22 0.9";
        private const string ColorSidebarBtnNormal = "0.08 0.08 0.08 0.9";

        // State tracking
        private HashSet<ulong> openUIs = new HashSet<ulong>();
        private Dictionary<ulong, int> playerGrades = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> playerTabs = new Dictionary<ulong, string>();
        private Dictionary<ulong, int> playerPages = new Dictionary<ulong, int>();
        private Dictionary<ulong, string> pendingDeletions = new Dictionary<ulong, string>();

        // Localization Messages
        private readonly Dictionary<string, string> messagesEn = new Dictionary<string, string>
        {
            ["SettingsTitle"] = "<b>SETTINGS</b>",
            ["AutoGradeTitle"] = "AUTO UPGRADE (BGRADE)",
            ["AutoGradeDesc"] = "Select the grade level that will be automatically applied when building blocks are placed.",
            ["AutoSkinTitle"] = "AUTOMATIC SKINS (BUILDING SKINS)",
            ["AutoSkinDesc"] = "Configure automatic skins for your building blocks. Opening the skins menu will close this dashboard.",
            ["ChooseSkinsBtn"] = "<b>CHOOSE SKINS</b>",
            ["BuildingsTitle"] = "<b>BUILDINGS LIST</b>",
            ["NoBuildingsFound"] = "No saved CopyPaste structures found.",
            ["PasteBtn"] = "Paste",
            ["RestoreBtn"] = "Restore",
            ["DeleteBtn"] = "Delete",
            ["ConfirmDeleteTitle"] = "Are you sure you want to delete structure\n<b><color=red>{0}</color></b>?",
            ["ConfirmDeleteYes"] = "<b>YES, DELETE</b>",
            ["ConfirmDeleteCancel"] = "<b>CANCEL</b>",
            ["DeleteSuccess"] = "<color=orange>SmartBuilderMenu</color>: Structure file '<color=red>{0}</color>' successfully deleted.",
            ["DeleteNotFound"] = "<color=orange>SmartBuilderMenu</color>: File '<color=red>{0}</color>' not found.",
            ["DeleteError"] = "<color=orange>SmartBuilderMenu</color>: Error deleting file: {0}",
            ["CloseBtn"] = "<b>CLOSE</b>",
            ["SettingsTab"] = "SETTINGS",
            ["BuildingsTab"] = "BUILDINGS",
            ["Twig"] = "Twig",
            ["Wood"] = "Wood",
            ["Stone"] = "Stone",
            ["Metal"] = "Metal",
            ["HQM"] = "HQM"
        };

        private readonly Dictionary<string, string> messagesUk = new Dictionary<string, string>
        {
            ["SettingsTitle"] = "<b>НАЛАШТУВАННЯ</b>",
            ["AutoGradeTitle"] = "АВТОМАТИЧНИЙ РІВЕНЬ БУДІВНИЦТВА (BGRADE)",
            ["AutoGradeDesc"] = "Оберіть рівень покращення, який буде автоматично застосовуватись під час встановлення блоків.",
            ["AutoSkinTitle"] = "АВТОМАТИЧНІ СКІНИ БУДІВЕЛЬ (BUILDING SKINS)",
            ["AutoSkinDesc"] = "Налаштуйте автоматичні скіни для ваших будівельних блоків. Відкриття меню налаштування скінів закриє цю панель.",
            ["ChooseSkinsBtn"] = "<b>НАЛАШТУВАТИ СКІНИ</b>",
            ["BuildingsTitle"] = "<b>СПИСОК ЗБЕРЕЖЕНИХ БУДІВЕЛЬ</b>",
            ["NoBuildingsFound"] = "Не знайдено збережених споруд у папці oxide/data/copypaste/",
            ["PasteBtn"] = "Вставити",
            ["RestoreBtn"] = "Вернути",
            ["DeleteBtn"] = "Видалити",
            ["ConfirmDeleteTitle"] = "Ви дійсно бажаєте видалити збережену споруду\n<b><color=red>{0}</color></b>?",
            ["ConfirmDeleteYes"] = "<b>ТАК, ВИДАЛИТИ</b>",
            ["ConfirmDeleteCancel"] = "<b>СКАСУВАТИ</b>",
            ["DeleteSuccess"] = "<color=orange>SmartBuilderMenu</color>: Файл споруди '<color=red>{0}</color>' успішно видалено.",
            ["DeleteNotFound"] = "<color=orange>SmartBuilderMenu</color>: Файл '<color=red>{0}</color>' не знайдено.",
            ["DeleteError"] = "<color=orange>SmartBuilderMenu</color>: Помилка видалення файлу: {0}",
            ["CloseBtn"] = "<b>ЗАКРИТИ</b>",
            ["SettingsTab"] = "НАЛАШТУВАННЯ",
            ["BuildingsTab"] = "БУДІВЛІ",
            ["Twig"] = "Солома",
            ["Wood"] = "Дерево",
            ["Stone"] = "Камінь",
            ["Metal"] = "Залізо",
            ["HQM"] = "МВК"
        };

        private string GetMsg(string key, string userId = null)
        {
            return lang.GetMessage(key, this, userId);
        }

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            lang.RegisterMessages(messagesEn, this, "en");
            lang.RegisterMessages(messagesUk, this, "uk");
        }

        private void Unload()
        {
            foreach (var userId in openUIs)
            {
                var player = BasePlayer.FindByID(userId);
                if (player != null)
                {
                    CuiHelper.DestroyUi(player, UIName);
                }
            }
            openUIs.Clear();
            playerGrades.Clear();
            playerTabs.Clear();
            playerPages.Clear();
            pendingDeletions.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            openUIs.Remove(player.userID);
            playerGrades.Remove(player.userID);
            playerTabs.Remove(player.userID);
            playerPages.Remove(player.userID);
            pendingDeletions.Remove(player.userID);
        }

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
            pendingDeletions.Remove(player.userID);
        }

        private void OpenMenu(BasePlayer player)
        {
            // Знищуємо тільки UI шар без очищення стану pendingDeletions
            CuiHelper.DestroyUi(player, UIName);
            openUIs.Add(player.userID);

            // Закриваємо меню вибору скінів з buildingskins.cs при відкриванні нашого меню
            CuiHelper.DestroyUi(player, "UI_BuildingLayer");

            // За замовчуванням відкриваємо вкладку налаштувань
            string activeTab = "settings";
            if (playerTabs.ContainsKey(player.userID))
            {
                activeTab = playerTabs[player.userID];
            }
            else
            {
                playerTabs[player.userID] = activeTab;
            }

            var elements = new CuiElementContainer();

            // 1. Головна повноекранна панель
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorBackground },
                RectTransform = { AnchorMin = "0.0 0.0", AnchorMax = "1.0 1.0" },
                CursorEnabled = true
            }, "Overlay", UIName);

            // 2. Ліве бічне меню (Sidebar)
            string sidebarName = UIName + ".Sidebar";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorSidebar },
                RectTransform = { AnchorMin = "0.0 0.0", AnchorMax = "0.22 1.0" }
            }, UIName, sidebarName);

            // Заголовок лівого меню
            elements.Add(new CuiLabel
            {
                Text = { Text = "SMART BUILDER", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = ColorTextGreen, Font = "robotocondensed-bold" },
                RectTransform = { AnchorMin = "0.0 0.85", AnchorMax = "1.0 0.98" }
            }, sidebarName);

            // Кнопка: Налаштування
            bool isSettingsActive = (activeTab == "settings");
            string settingsBtnColor = isSettingsActive ? ColorSidebarBtnActive : ColorSidebarBtnNormal;
            string settingsTxtColor = isSettingsActive ? "1 1 1 1" : "0.7 0.7 0.7 1";
            string settingsText = isSettingsActive ? $"<b>{GetMsg("SettingsTab", player.UserIDString)}</b>" : GetMsg("SettingsTab", player.UserIDString);
            CreateButton(elements, sidebarName, settingsText, "smartmenu_cmd tab_settings", "0.05 0.70", "0.95 0.78", settingsBtnColor, 13, settingsTxtColor);

            // Кнопка: Будівлі
            bool isBuildingsActive = (activeTab == "buildings");
            string buildingsBtnColor = isBuildingsActive ? ColorSidebarBtnActive : ColorSidebarBtnNormal;
            string buildingsTxtColor = isBuildingsActive ? "1 1 1 1" : "0.7 0.7 0.7 1";
            string buildingsText = isBuildingsActive ? $"<b>{GetMsg("BuildingsTab", player.UserIDString)}</b>" : GetMsg("BuildingsTab", player.UserIDString);
            CreateButton(elements, sidebarName, buildingsText, "smartmenu_cmd tab_buildings", "0.05 0.60", "0.95 0.68", buildingsBtnColor, 13, buildingsTxtColor);

            // Кнопка: Закрити меню (знизу сайдбару)
            CreateButton(elements, sidebarName, GetMsg("CloseBtn", player.UserIDString), "smartmenu_cmd close", "0.05 0.05", "0.95 0.13", ColorRedBtn, 13, "1 1 1 1");

            // 3. Контентна область справа
            string contentName = UIName + ".Content";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorContent },
                RectTransform = { AnchorMin = "0.22 0.0", AnchorMax = "1.0 1.0" }
            }, UIName, contentName);

            // Відображаємо відповідну вкладку
            if (activeTab == "settings")
            {
                RenderSettingsTab(elements, contentName, player);
            }
            else if (activeTab == "buildings")
            {
                RenderBuildingsTab(elements, contentName, player);
            }

            // 4. Додаткове модальне вікно підтвердження видалення
            if (pendingDeletions.TryGetValue(player.userID, out string deleteFile) && !string.IsNullOrEmpty(deleteFile))
            {
                RenderDeleteConfirmationModal(elements, player, deleteFile);
            }

            CuiHelper.AddUi(player, elements);
        }

        private void RenderSettingsTab(CuiElementContainer elements, string parent, BasePlayer player)
        {
            // Назва вкладки
            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("SettingsTitle", player.UserIDString), FontSize = 22, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.90", AnchorMax = "0.95 0.98" }
            }, parent);

            // --- РОЗДІЛ AUTO GRADE ---
            string gradeSection = parent + ".AutoGrade";
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.4" },
                RectTransform = { AnchorMin = "0.05 0.48", AnchorMax = "0.95 0.85" }
            }, parent, gradeSection);

            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("AutoGradeTitle", player.UserIDString), FontSize = 14, Align = TextAnchor.MiddleLeft, Color = ColorTextGreen, Font = "robotocondensed-bold" },
                RectTransform = { AnchorMin = "0.03 0.82", AnchorMax = "0.97 0.95" }
            }, gradeSection);

            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("AutoGradeDesc", player.UserIDString), FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.03 0.65", AnchorMax = "0.97 0.80" }
            }, gradeSection);

            // Визначаємо поточний вибраний рівень
            int currentGrade = -1;
            if (playerGrades.TryGetValue(player.userID, out int grade))
            {
                currentGrade = grade;
            }

            var gradeButtons = new List<ValueTuple<string, int, string>>
            {
                (GetMsg("Twig", player.UserIDString), 0, "0.4 0.4 0.4 0.8"),
                (GetMsg("Wood", player.UserIDString), 1, "0.55 0.35 0.15 0.8"),
                (GetMsg("Stone", player.UserIDString), 2, "0.6 0.6 0.6 0.8"),
                (GetMsg("Metal", player.UserIDString), 3, "0.7 0.3 0.2 0.8"),
                (GetMsg("HQM", player.UserIDString), 4, "0.2 0.6 0.7 0.8")
            };

            float btnWidth = 0.16f;
            float btnGap = 0.025f;
            for (int j = 0; j < gradeButtons.Count; j++)
            {
                var btn = gradeButtons[j];
                float leftX = 0.03f + j * (btnWidth + btnGap);
                float rightX = leftX + btnWidth;

                bool isActive = (currentGrade == btn.Item2);
                string btnColor = isActive ? ColorBtnActive : btn.Item3;
                string text = isActive ? $"<b>{btn.Item1}</b>" : btn.Item1;
                string txtColor = isActive ? "1 1 1 1" : "0.8 0.8 0.8 1";

                string command = $"smartmenu_cmd bgrade_{btn.Item2}";

                CreateButton(elements, gradeSection, text, command, $"{leftX} 0.15", $"{rightX} 0.55", btnColor, 12, txtColor);
            }

            // --- РОЗДІЛ AUTO SKIN ---
            string skinSection = parent + ".AutoSkin";
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.4" },
                RectTransform = { AnchorMin = "0.05 0.10", AnchorMax = "0.43 0.43" }
            }, parent, skinSection);

            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("AutoSkinTitle", player.UserIDString), FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.8 0.5 0.1 1.0", Font = "robotocondensed-bold" },
                RectTransform = { AnchorMin = "0.03 0.80", AnchorMax = "0.97 0.95" }
            }, skinSection);

            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("AutoSkinDesc", player.UserIDString), FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.03 0.50", AnchorMax = "0.97 0.75" }
            }, skinSection);

            // Кнопка відкриття меню скінів
            CreateButton(elements, skinSection, GetMsg("ChooseSkinsBtn", player.UserIDString), "smartmenu_cmd bskin", "0.03 0.15", "0.97 0.42", "0.8 0.5 0.1 0.9", 12, "1 1 1 1");
        }

        private void RenderBuildingsTab(CuiElementContainer elements, string parent, BasePlayer player)
        {
            // Назва вкладки
            elements.Add(new CuiLabel
            {
                Text = { Text = GetMsg("BuildingsTitle", player.UserIDString), FontSize = 22, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.90", AnchorMax = "0.95 0.98" }
            }, parent);

            // Отримуємо список файлів з oxide/data/copypaste/
            var fileList = new List<string>();
            try
            {
                var files = Interface.Oxide.DataFileSystem.GetFiles("copypaste/");
                foreach (var file in files)
                {
                    var justfile = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(justfile))
                    {
                        fileList.Add(justfile);
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Помилка читання директорії CopyPaste: {ex.Message}");
            }

            fileList.Sort();

            // Індекс сторінки
            int page = 0;
            if (playerPages.TryGetValue(player.userID, out int p))
            {
                page = p;
            }
            else
            {
                playerPages[player.userID] = 0;
            }

            int totalPages = (fileList.Count + 7) / 8;
            if (totalPages == 0) totalPages = 1;

            if (page >= totalPages)
            {
                page = totalPages - 1;
                playerPages[player.userID] = page;
            }
            if (page < 0)
            {
                page = 0;
                playerPages[player.userID] = 0;
            }

            if (fileList.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = GetMsg("NoBuildingsFound", player.UserIDString), FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.6 1" },
                    RectTransform = { AnchorMin = "0.05 0.40", AnchorMax = "0.95 0.60" }
                }, parent);
                return;
            }

            int startIndex = page * 8;
            int count = Math.Min(8, fileList.Count - startIndex);
            List<string> pageFiles = fileList.GetRange(startIndex, count);

            float rowHeight = 0.08f;
            float rowGap = 0.015f;

            for (int i = 0; i < pageFiles.Count; i++)
            {
                string filename = pageFiles[i];
                float topY = 0.85f - i * (rowHeight + rowGap);
                float botY = topY - rowHeight;

                string rowPanelName = parent + $".Row_{i}";
                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorCard },
                    RectTransform = { AnchorMin = $"0.05 {botY}", AnchorMax = $"0.95 {topY}" }
                }, parent, rowPanelName);

                // Назва файлу споруди
                elements.Add(new CuiLabel
                {
                    Text = { Text = $"<b>{filename}</b>", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                    RectTransform = { AnchorMin = "0.03 0.0", AnchorMax = "0.40 1.0" }
                }, rowPanelName);

                // Кнопка вставки (Paste)
                CreateButton(elements, rowPanelName, GetMsg("PasteBtn", player.UserIDString), $"smartmenu_cmd paste \"{filename}\"", "0.45 0.12", "0.60 0.88", ColorGreenBtn, 11);

                // Кнопка відновлення на старе місце (Restore / Pasteback)
                CreateButton(elements, rowPanelName, GetMsg("RestoreBtn", player.UserIDString), $"smartmenu_cmd restore \"{filename}\"", "0.63 0.12", "0.78 0.88", ColorBlueBtn, 11);

                // Кнопка видалення файлу (Delete)
                CreateButton(elements, rowPanelName, GetMsg("DeleteBtn", player.UserIDString), $"smartmenu_cmd delete \"{filename}\"", "0.81 0.12", "0.96 0.88", ColorRedBtn, 11);
            }

            // Навігація сторінок (Pagination)
            string paginationPanel = parent + ".Pagination";
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.05 0.01", AnchorMax = "0.95 0.05" }
            }, parent, paginationPanel);

            // Попередня сторінка
            if (page > 0)
            {
                CreateButton(elements, paginationPanel, "<b>< Prev</b>", "smartmenu_cmd prev_page", "0.30 0.0", "0.42 1.0", ColorGreyBtn, 12);
            }

            // Індикатор сторінок
            elements.Add(new CuiLabel
            {
                Text = { Text = $"Page {page + 1} / {totalPages}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.44 0.0", AnchorMax = "0.56 1.0" }
            }, paginationPanel);

            // Наступна сторінка
            if ((page + 1) * 8 < fileList.Count)
            {
                CreateButton(elements, paginationPanel, "<b>Next ></b>", "smartmenu_cmd next_page", "0.58 0.0", "0.70 1.0", ColorGreyBtn, 12);
            }
        }

        private void RenderDeleteConfirmationModal(CuiElementContainer elements, BasePlayer player, string deleteFile)
        {
            string modalName = UIName + ".DeleteConfirmModal";

            // Темний напівпрозорий фон для затемнення решти інтерфейсу
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.0 0.0 0.0 0.6" },
                RectTransform = { AnchorMin = "0.0 0.0", AnchorMax = "1.0 1.0" }
            }, UIName, modalName);

            // Контейнер модального вікна
            string boxName = modalName + ".Box";
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.98" },
                RectTransform = { AnchorMin = "0.32 0.36", AnchorMax = "0.68 0.60" }
            }, modalName, boxName);

            // Червона декоративна смуга зверху
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorRedBtn },
                RectTransform = { AnchorMin = "0.0 0.96", AnchorMax = "1.0 1.0" }
            }, boxName);

            // Текст питання
            elements.Add(new CuiLabel
            {
                Text = { Text = string.Format(GetMsg("ConfirmDeleteTitle", player.UserIDString), deleteFile), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.45", AnchorMax = "0.95 0.88" }
            }, boxName);

            // Кнопка підтвердження видалення
            CreateButton(elements, boxName, GetMsg("ConfirmDeleteYes", player.UserIDString), $"smartmenu_cmd confirmdelete", "0.10 0.15", "0.46 0.38", ColorRedBtn, 11);

            // Кнопка скасування
            CreateButton(elements, boxName, GetMsg("ConfirmDeleteCancel", player.UserIDString), $"smartmenu_cmd canceldelete", "0.54 0.15", "0.90 0.38", ColorGreyBtn, 11);
        }

        private void CreateButton(CuiElementContainer container, string parent, string text, string command, string anchorMin, string anchorMax, string color, int fontSize = 14, string textColor = "1 1 1 1")
        {
            container.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Text = { Text = text, FontSize = fontSize, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent);
        }

        [ConsoleCommand("smartmenu_cmd")]
        private void CmdMenuAction(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse)) return;

            string action = (string)arg.Args[0];
            string filename = arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)) : string.Empty;

            // Очищення лапок, якщо вони є у переданому імені файлу
            if (filename.StartsWith("\"") && filename.EndsWith("\"") && filename.Length > 1)
            {
                filename = filename.Substring(1, filename.Length - 2);
            }

            if (action == "close")
            {
                CloseMenu(player);
                return;
            }

            if (action == "tab_settings")
            {
                playerTabs[player.userID] = "settings";
                OpenMenu(player);
                return;
            }

            if (action == "tab_buildings")
            {
                playerTabs[player.userID] = "buildings";
                OpenMenu(player);
                return;
            }

            if (action == "prev_page")
            {
                if (playerPages.ContainsKey(player.userID))
                {
                    playerPages[player.userID]--;
                }
                OpenMenu(player);
                return;
            }

            if (action == "next_page")
            {
                if (playerPages.ContainsKey(player.userID))
                {
                    playerPages[player.userID]++;
                }
                OpenMenu(player);
                return;
            }

            if (action.StartsWith("bgrade_"))
            {
                string levelStr = action.Split('_')[1];
                if (int.TryParse(levelStr, out int level))
                {
                    playerGrades[player.userID] = level;
                    player.SendConsoleCommand($"chat.say \"/bgrade {level}\"");
                }
                OpenMenu(player);
                return;
            }

            if (action == "bskin")
            {
                CloseMenu(player);
                player.SendConsoleCommand("chat.say \"/bskin\"");
                return;
            }

            if (action == "paste")
            {
                CloseMenu(player);
                player.SendConsoleCommand($"chat.say \"/paste \\\"{filename}\\\"\"");
                return;
            }

            if (action == "restore")
            {
                CloseMenu(player);
                player.SendConsoleCommand($"chat.say \"/pasteback \\\"{filename}\\\"\"");
                return;
            }

            if (action == "delete")
            {
                pendingDeletions[player.userID] = filename;
                OpenMenu(player);
                return;
            }

            if (action == "confirmdelete")
            {
                if (pendingDeletions.TryGetValue(player.userID, out string fileToDelete) && !string.IsNullOrEmpty(fileToDelete))
                {
                    try
                    {
                        var path = Path.Combine(Interface.Oxide.DataDirectory, "copypaste", $"{fileToDelete}.json");
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            player.ChatMessage(string.Format(GetMsg("DeleteSuccess", player.UserIDString), fileToDelete));
                        }
                        else
                        {
                            player.ChatMessage(string.Format(GetMsg("DeleteNotFound", player.UserIDString), fileToDelete));
                        }
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Помилка видалення файлу CopyPaste: {ex.Message}");
                        player.ChatMessage(string.Format(GetMsg("DeleteError", player.UserIDString), ex.Message));
                    }
                    pendingDeletions.Remove(player.userID);
                }
                OpenMenu(player);
                return;
            }

            if (action == "canceldelete")
            {
                pendingDeletions.Remove(player.userID);
                OpenMenu(player);
                return;
            }
        }
    }
}