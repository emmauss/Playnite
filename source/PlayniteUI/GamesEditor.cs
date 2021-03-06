﻿using LiteDB;
using NLog;
using Playnite;
using Playnite.Database;
using Playnite.Models;
using Playnite.Providers;
using Playnite.Providers.Steam;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteUI.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Shell;

namespace PlayniteUI
{
    public class GamesEditor : INotifyPropertyChanged, IDisposable
    {
        private static NLog.Logger logger = LogManager.GetCurrentClassLogger();
        private IResourceProvider resources = new ResourceProvider();
        private GameDatabase database;
        private IDialogsFactory dialogs;
        private Settings appSettings;

        public bool IsFullscreen
        {
            get; set;
        }

        public GameControllerFactory Controllers
        {
            get; private set;
        }

        public List<Game> LastGames
        {
            get
            {
                return database.GamesCollection?.Find(Query.And(Query.Not("LastActivity", null), Query.EQ("State.Installed", true))).OrderByDescending(a => a.LastActivity).Take(10).ToList();              
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public GamesEditor(GameDatabase database, Settings appSettings, IDialogsFactory dialogs)
        {
            this.dialogs = dialogs;
            this.database = database;
            this.appSettings = appSettings;
            Controllers = new GameControllerFactory(database);
            Controllers.Installed += Controllers_Installed;
            Controllers.Uninstalled += Controllers_Uninstalled;
            Controllers.Started += Controllers_Started;
            Controllers.Stopped += Controllers_Stopped;            
        }

        public void Dispose()
        {
            foreach (var controller in Controllers.Controllers)
            {                
                UpdateGameState(controller.Game.Id, null, false, false, false, false);
            }

            Controllers?.Dispose();
        }

        public void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public bool? SetGameCategories(Game game)
        {
            var model = new CategoryConfigViewModel(CategoryConfigWindowFactory.Instance, database, game, true);
            return model.OpenView();
        }

        public bool? SetGamesCategories(List<Game> games)
        {
            var model = new CategoryConfigViewModel(CategoryConfigWindowFactory.Instance, database, games, true);
            return model.OpenView();
        }

        public bool? EditGame(Game game)
        {
            var model = new GameEditViewModel(
                            game,
                            database,
                            GameEditWindowFactory.Instance,
                            new DialogsFactory(),
                            new ResourceProvider(),
                            appSettings);
            return model.OpenView();
        }

        public bool? EditGames(List<Game> games)
        {
            var model = new GameEditViewModel(
                            games,
                            database,
                            GameEditWindowFactory.Instance,
                            new DialogsFactory(),
                            new ResourceProvider(),
                            appSettings);
            return model.OpenView();
        }

        public void PlayGame(Game game)
        {
            if (!game.IsInstalled)
            {
                InstallGame(game);
                return;
            }

            logger.Info($"Starting {game.GetIdentifierInfo()}");
            var dbGame = database.GetGame(game.Id);
            if (dbGame == null)
            {
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameStartErrorNoGame"), game.Name),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateJumpList();
                return;
            }

            IGameController controller = null;

            try
            {
                controller = GameControllerFactory.GetGameBasedController(game, appSettings);
                Controllers.RemoveController(game.Id);
                Controllers.AddController(controller);
                UpdateGameState(game.Id, null, null, null, null, true);
                controller.Play(database.GetEmulators());
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                if (controller != null)
                {
                    Controllers.RemoveController(game.Id);
                    UpdateGameState(game.Id, null, null, null, null, false);
                }

                logger.Error(exc, "Cannot start game: ");
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameStartError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                UpdateJumpList();                
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to set jump list data: ");
            }
        }

        public void ActivateAction(Game game, GameTask action)
        {
            try
            {
                GameHandler.ActivateTask(action, game, database.EmulatorsCollection.FindAll().ToList());
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameStartActionError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void OpenGameLocation(Game game)
        {
            if (string.IsNullOrEmpty(game.InstallDirectory))
            {
                return;
            }

            try
            {
                Process.Start(game.ResolveVariables(game.InstallDirectory));
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameOpenLocationError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void SetHideGame(Game game, bool state)
        {
            game.Hidden = state;
            database.UpdateGameInDatabase(game);
        }

        public void SetHideGames(List<Game> games, bool state)
        {
            foreach (var game in games)
            {
                SetHideGame(game, state);
            }
        }

        public void ToggleHideGame(Game game)
        {
            game.Hidden = !game.Hidden;
            database.UpdateGameInDatabase(game);
        }

        public void ToggleHideGames(List<Game> games)
        {
            foreach (var game in games)
            {
                ToggleHideGame(game);
            }
        }

        public void SetFavoriteGame(Game game, bool state)
        {
            game.Favorite = state;
            database.UpdateGameInDatabase(game);
        }

        public void SetFavoriteGames(List<Game> games, bool state)
        {
            foreach (var game in games)
            {
                SetFavoriteGame(game, state);
            }
        }

        public void ToggleFavoriteGame(Game game)
        {
            game.Favorite = !game.Favorite;
            database.UpdateGameInDatabase(game);
        }

        public void ToggleFavoriteGame(List<Game> games)
        {
            foreach (var game in games)
            {
                ToggleFavoriteGame(game);
            }
        }

        public void RemoveGame(Game game)
        {
            if (game.State.Installing || game.State.Running || game.State.Launching || game.State.Uninstalling)
            {
                dialogs.ShowMessage(
                    resources.FindString("LOCGameRemoveRunningError"),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (dialogs.ShowMessage(
                resources.FindString("LOCGameRemoveAskMessage"),
                resources.FindString("LOCGameRemoveAskTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            database.DeleteGame(game);
        }

        public void RemoveGames(List<Game> games)
        {
            if (games.Exists(a => a.State.Installing || a.State.Running || a.State.Launching || a.State.Uninstalling))
            {
                dialogs.ShowMessage(
                    resources.FindString("LOCGameRemoveRunningError"),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (dialogs.ShowMessage(
                string.Format(resources.FindString("LOCGamesRemoveAskMessage"), games.Count()),
                resources.FindString("LOCGameRemoveAskTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            database.DeleteGames(games);
        }

        public void CreateShortcut(Game game)
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(Path.Combine("%userprofile%", "Desktop", FileSystem.GetSafeFilename(game.Name) + ".lnk"));
                string icon = string.Empty;

                if (!string.IsNullOrEmpty(game.Icon) && Path.GetExtension(game.Icon) == ".ico")
                {
                    FileSystem.CreateDirectory(Path.Combine(Paths.DataCachePath, "icons"));
                    icon = Path.Combine(Paths.DataCachePath, "icons", game.Id + ".ico");
                    database.SaveFile(game.Icon, icon);
                }
                else if (game.PlayTask?.Type == GameTaskType.File)
                {
                    if (Path.IsPathRooted(game.ResolveVariables(game.PlayTask.Path)))
                    {
                        icon = game.ResolveVariables(game.PlayTask.Path);
                    }
                    else
                    {
                        icon = Path.Combine(game.ResolveVariables(game.PlayTask.WorkingDir), game.ResolveVariables(game.PlayTask.Path));
                    }
                }

                Programs.CreateShortcut(Paths.ExecutablePath, "-command launch:" + game.Id, icon, path);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to create shortcut: ");
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameShortcutError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CreateShortcuts(List<Game> games)
        {
            foreach (var game in games)
            {
                CreateShortcut(game);
            }
        }

        public void InstallGame(Game game)
        {
            logger.Info($"Installing {game.GetIdentifierInfo()}");
            IGameController controller = null;
            try
            {
                controller = GameControllerFactory.GetGameBasedController(game, appSettings);
                Controllers.RemoveController(game.Id);
                Controllers.AddController(controller);
                UpdateGameState(game.Id, null, null, true, null, null);
                controller.Install();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                if (controller != null)
                {
                    Controllers.RemoveController(game.Id);
                    UpdateGameState(game.Id, null, null, false, null, null);
                }

                logger.Error(exc, "Cannot install game: ");
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameInstallError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UnInstallGame(Game game)
        {
            if (game.State.Running || game.State.Launching)
            {
                dialogs.ShowMessage(
                    resources.FindString("LOCGameUninstallRunningError"),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            logger.Info($"Uninstalling {game.GetIdentifierInfo()}");
            IGameController controller = null;

            try
            {
                controller = GameControllerFactory.GetGameBasedController(game, appSettings);
                Controllers.RemoveController(game.Id);
                Controllers.AddController(controller);
                UpdateGameState(game.Id, null, null, null, true, null);
                controller.Uninstall();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                if (controller != null)
                {
                    Controllers.RemoveController(game.Id);
                    UpdateGameState(game.Id, null, null, null, false, null);
                }

                logger.Error(exc, "Cannot un-install game: ");
                dialogs.ShowMessage(
                    string.Format(resources.FindString("LOCGameUninstallError"), exc.Message),
                    resources.FindString("LOCGameError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UpdateJumpList()
        {           
            OnPropertyChanged("LastGames");
            var jumpList = new JumpList();
            foreach (var lastGame in LastGames)
            {
                JumpTask task = new JumpTask
                {
                    Title = lastGame.Name,
                    Arguments = "-command launch:" + lastGame.Id,
                    Description = string.Empty,
                    CustomCategory = "Recent",
                    ApplicationPath = Paths.ExecutablePath
                };

                if (lastGame.PlayTask?.Type == GameTaskType.File)
                {
                    if (Path.IsPathRooted(lastGame.ResolveVariables(lastGame.PlayTask.Path)))
                    {
                        task.IconResourcePath = lastGame.ResolveVariables(lastGame.PlayTask.Path);
                    }
                    else
                    {
                        var workDir = lastGame.ResolveVariables(lastGame.PlayTask.WorkingDir);
                        var path = lastGame.ResolveVariables(lastGame.PlayTask.Path);
                        if (string.IsNullOrEmpty(workDir))
                        {
                            task.IconResourcePath = path;
                        }
                        else
                        {
                            task.IconResourcePath = Path.Combine(workDir, path);
                        }
                    }
                }

                jumpList.JumpItems.Add(task);
                jumpList.ShowFrequentCategory = false;
                jumpList.ShowRecentCategory = false;
            }
            
            JumpList.SetJumpList(Application.Current, jumpList);
        }

        public void CancelGameMonitoring(Game game)
        {
            Controllers.RemoveController(game.Id);
            UpdateGameState(game.Id, null, false, false, false, false);
        }

        private void UpdateGameState(int id, bool? installed, bool? running, bool? installing, bool? uninstalling, bool? launching)
        {
            var game = database.GetGame(id);
            game.State.SetState(installed, running, installing, uninstalling, launching);
            if (launching == true)
            {
                game.LastActivity = DateTime.Now;
                game.PlayCount += 1;
                if (game.CompletionStatus == CompletionStatus.NotPlayed)
                {
                    game.CompletionStatus = CompletionStatus.Played;
                }
            }

            database.UpdateGameInDatabase(game);
        }

        private void Controllers_Started(object sender, GameControllerEventArgs args)
        {
            var game = args.Controller.Game;
            logger.Info($"Started {game.Name} game.");
            UpdateGameState(game.Id, null, true, null, null, false);

            if (!IsFullscreen)
            {
                if (appSettings.AfterLaunch == AfterLaunchOptions.Close)
                {
                    App.CurrentApp.Quit();
                }
                else if (appSettings.AfterLaunch == AfterLaunchOptions.Minimize)
                {
                    Application.Current.MainWindow.WindowState = WindowState.Minimized;
                }
            }
        }

        private void Controllers_Stopped(object sender, GameControllerEventArgs args)
        {
            var game = args.Controller.Game;
            logger.Info($"Game {game.Name} stopped after {args.EllapsedTime} seconds.");

            var dbGame = database.GetGame(game.Id);
            dbGame.State.Running = false;
            dbGame.Playtime += args.EllapsedTime;
            database.UpdateGameInDatabase(dbGame);
            Controllers.RemoveController(args.Controller);
        }

        private void Controllers_Installed(object sender, GameControllerEventArgs args)
        {
            var game = args.Controller.Game;
            logger.Info($"Game {game.Name} installed after {args.EllapsedTime} seconds.");

            var dbGame = database.GetGame(game.Id);
            dbGame.State.Installing = false;
            dbGame.State.Installed = true;
            dbGame.InstallDirectory = args.Controller.Game.InstallDirectory;

            if (dbGame.PlayTask == null)
            {
                dbGame.PlayTask = args.Controller.Game.PlayTask;
            }

            if (dbGame.OtherTasks == null)
            {
                dbGame.OtherTasks = args.Controller.Game.OtherTasks;
            }

            database.UpdateGameInDatabase(dbGame);
            Controllers.RemoveController(args.Controller);
        }

        private void Controllers_Uninstalled(object sender, GameControllerEventArgs args)
        {
            var game = args.Controller.Game;
            logger.Info($"Game {game.Name} uninstalled after {args.EllapsedTime} seconds.");

            var dbGame = database.GetGame(game.Id);
            dbGame.State.Uninstalling = false;
            dbGame.State.Installed = false;
            dbGame.InstallDirectory = string.Empty;
            database.UpdateGameInDatabase(dbGame);
            Controllers.RemoveController(args.Controller);
        }
    }
}
