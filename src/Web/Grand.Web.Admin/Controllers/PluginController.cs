﻿using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Business.Core.Utilities.Common.Security;
using Grand.Infrastructure;
using Grand.Infrastructure.Configuration;
using Grand.Infrastructure.Plugins;
using Grand.SharedKernel.Extensions;
using Grand.Web.Admin.Extensions;
using Grand.Web.Admin.Extensions.Mapping;
using Grand.Web.Admin.Models.Plugins;
using Grand.Web.Common.DataSource;
using Grand.Web.Common.Extensions;
using Grand.Web.Common.Security.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Reflection;

namespace Grand.Web.Admin.Controllers
{
    [PermissionAuthorize(PermissionSystemName.Plugins)]
    public class PluginController : BaseAdminController
    {
        #region Fields

        private readonly ITranslationService _translationService;
        private readonly ILogger<PluginController> _logger;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly IServiceProvider _serviceProvider;
        private readonly IWorkContext _workContext;
        private readonly ExtensionsConfig _extConfig;

        #endregion

        #region Constructors

        public PluginController(
            ITranslationService translationService,
            ILogger<PluginController> logger,
            IHostApplicationLifetime applicationLifetime,
            IWorkContext workContext,
            IServiceProvider serviceProvider,
            ExtensionsConfig extConfig)
        {
            _translationService = translationService;
            _logger = logger;
            _workContext = workContext;
            _applicationLifetime = applicationLifetime;
            _serviceProvider = serviceProvider;
            _extConfig = extConfig;
        }

        #endregion

        #region Utilities

        [NonAction]
        protected virtual PluginModel PreparePluginModel(PluginInfo PluginInfo)
        {
            var pluginModel = PluginInfo.ToModel();
            //logo
            pluginModel.LogoUrl = PluginInfo.GetLogoUrl(_workContext);

            //configuration URLs
            if (PluginInfo.Installed)
            {
                var pluginInstance = PluginInfo.Instance(_serviceProvider);
                pluginModel.ConfigurationUrl = pluginInstance.ConfigurationUrl();
            }

            return pluginModel;
        }

        /// <summary>
        ///  Depth-first recursive delete, with handling for descendant directories open in Windows Explorer.
        /// </summary>
        /// <param name="path">Directory path</param>
        protected void DeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(path);

            //find more info about directory deletion
            //and why we use this approach at https://stackoverflow.com/questions/329355/cannot-delete-directory-with-directory-deletepath-true

            foreach (var directory in Directory.GetDirectories(path))
            {
                DeleteDirectory(directory);
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                Directory.Delete(path, true);
            }
        }

        protected byte[] ToByteArray(Stream stream)
        {
            using (stream)
            {
                using (MemoryStream memStream = new MemoryStream())
                {
                    stream.CopyTo(memStream);
                    return memStream.ToArray();
                }
            }
        }

        #endregion

        #region Methods

        public IActionResult Index() => RedirectToAction("List");

        public IActionResult List()
        {
            var model = new PluginListModel {
                //load modes
                AvailableLoadModes = LoadPluginsStatus.All.ToSelectList(HttpContext, false).ToList()
            };

            return View(model);
        }

        [HttpPost]
        public IActionResult ListSelect(DataSourceRequest command, PluginListModel model)
        {
            var pluginInfos = PluginManager.ReferencedPlugins.ToList();
            var loadMode = (LoadPluginsStatus)model.SearchLoadModeId;
            switch (loadMode)
            {
                case LoadPluginsStatus.InstalledOnly:
                    pluginInfos = pluginInfos.Where(x => x.Installed).ToList();
                    break;
                case LoadPluginsStatus.NotInstalledOnly:
                    pluginInfos = pluginInfos.Where(x => !x.Installed).ToList();
                    break;
            }

            var items = new List<PluginModel>();
            foreach (var item in pluginInfos.OrderBy(x => x.Group))
            {
                items.Add(PreparePluginModel(item));
            }

            var gridModel = new DataSourceResult {
                Data = items,
                Total = pluginInfos.Count
            };
            return Json(gridModel);
        }

        [HttpPost]
        public async Task<IActionResult> Install(string systemName)
        {
            try
            {
                var pluginInfo = PluginManager.ReferencedPlugins.FirstOrDefault(x => x.SystemName == systemName);
                if (pluginInfo == null)
                    //No plugin found with the specified id
                    return RedirectToAction("List");

                if (pluginInfo.SupportedVersion != GrandVersion.SupportedPluginVersion)
                {
                    Error("You can't install unsupported version of plugin");
                    return RedirectToAction("List");
                }

                //check whether plugin is not installed
                if (pluginInfo.Installed)
                    return RedirectToAction("List");

                //install plugin
                var plugin = pluginInfo.Instance<IPlugin>(_serviceProvider);
                await plugin.Install();

                Success(_translationService.GetResource("Admin.Plugins.Installed"));

                _logger.LogInformation("The plugin has been installed by the user {CurrentCustomerEmail}",
                    _workContext.CurrentCustomer.Email);

                //stop application
                _applicationLifetime.StopApplication();
            }
            catch (Exception exc)
            {
                Error(exc);
            }

            return RedirectToAction("List");
        }

        [HttpPost]
        public async Task<IActionResult> Uninstall(string systemName)
        {
            try
            {
                var pluginInfo = PluginManager.ReferencedPlugins.FirstOrDefault(x => x.SystemName == systemName);
                if (pluginInfo == null)
                    //No plugin found with the specified id
                    return RedirectToAction("List");

                //check whether plugin is installed
                if (!pluginInfo.Installed)
                    return RedirectToAction("List");

                //uninstall plugin
                var plugin = pluginInfo.Instance<IPlugin>(_serviceProvider);
                await plugin.Uninstall();

                Success(_translationService.GetResource("Admin.Plugins.Uninstalled"));

                _logger.LogInformation("The plugin has been uninstalled by the user {CurrentCustomerEmail}",
                    _workContext.CurrentCustomer.Email);

                //stop application
                _applicationLifetime.StopApplication();
            }
            catch (Exception exc)
            {
                Error(exc);
            }

            return RedirectToAction("List");
        }

        [HttpPost]
        public IActionResult Remove(string systemName)
        {
            if (_extConfig.DisableUploadExtensions)
            {
                Error("Upload plugins is disable");
                return RedirectToAction("List");
            }

            try
            {
                var pluginInfo = PluginManager.ReferencedPlugins.FirstOrDefault(x => x.SystemName == systemName);
                if (pluginInfo == null)
                    //No plugin found with the specified id
                    return RedirectToAction("List");

                var pluginsPath = CommonPath.PluginsPath;

                foreach (var folder in Directory.GetDirectories(pluginsPath))
                {
                    if (Path.GetFileName(folder) != "bin" && Directory.GetFiles(folder).Select(x => Path.GetFileName(x))
                            .Contains(pluginInfo.PluginFileName))
                    {
                        DeleteDirectory(folder);
                    }
                }

                //uninstall plugin
                Success(_translationService.GetResource("Admin.Plugins.Removed"));

                _logger.LogInformation("The plugin has been removed by the user {CurrentCustomerEmail}",
                    _workContext.CurrentCustomer.Email);

                //stop application
                _applicationLifetime.StopApplication();
            }
            catch (Exception exc)
            {
                Error(exc);
            }

            return RedirectToAction("List");
        }

        public IActionResult ReloadList()
        {
            _logger.LogInformation("Reload list of plugins by the user {CurrentCustomerEmail}",
                _workContext.CurrentCustomer.Email);

            //stop application
            _applicationLifetime.StopApplication();
            return RedirectToAction("List");
        }


        [HttpPost]
        public IActionResult UploadPlugin(IFormFile zippedFile)
        {
            if (_extConfig.DisableUploadExtensions)
            {
                Error("Upload plugins is disable");
                return RedirectToAction("List");
            }

            if (zippedFile == null || zippedFile.Length == 0)
            {
                Error(_translationService.GetResource("Admin.Common.UploadFile"));
                return RedirectToAction("List");
            }

            var zipFilePath = "";
            try
            {
                if (!Path.GetExtension(zippedFile.FileName)
                        ?.Equals(".zip", StringComparison.InvariantCultureIgnoreCase) ?? true)
                    throw new Exception("Only zip archives are supported");

                //ensure that temp directory is created
                var tempDirectory = CommonPath.TmpUploadPath;
                Directory.CreateDirectory(new DirectoryInfo(tempDirectory).FullName);

                //copy original archive to the temp directory
                zipFilePath = Path.Combine(tempDirectory, zippedFile.FileName);
                using (var fileStream = new FileStream(zipFilePath, FileMode.Create))
                    zippedFile.CopyTo(fileStream);

                Upload(zipFilePath);

                var message = _translationService.GetResource("Admin.Plugins.Uploaded");
                Success(message);
            }
            finally
            {
                //delete temporary file
                if (!string.IsNullOrEmpty(zipFilePath))
                    System.IO.File.Delete(zipFilePath);
            }

            _logger.LogInformation("The plugin has been uploaded by the user {CurrentCustomerEmail}",
                _workContext.CurrentCustomer.Email);

            //stop application
            _applicationLifetime.StopApplication();

            return RedirectToAction("List");
        }

        private void Upload(string archivePath)
        {
            var pluginsDirectory = CommonPath.PluginsPath;
            var uploadedItemDirectoryName = "";
            PluginInfo _pluginInfo = null;
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                var rootDirectories = archive.Entries.Where(entry =>
                    entry.FullName.Count(ch => ch == '/') == 1 && entry.FullName.EndsWith("/")).ToList();
                if (rootDirectories.Count != 1)
                {
                    throw new Exception(
                        $"The archive should contain only one root plugin. For example, Payments.PayPalDirect.");
                }

                //get directory name (remove the ending /)
                uploadedItemDirectoryName = rootDirectories.First().FullName.TrimEnd('/');

                var supportedVersion = false;
                var _fpath = "";
                foreach (var entry in archive.Entries.Where(x => x.FullName.Contains(".dll")))
                {
                    using var unzippedEntryStream = entry.Open();
                    try
                    {
                        var assembly = Assembly.Load(ToByteArray(unzippedEntryStream));
                        var pluginInfo = assembly.GetCustomAttribute<PluginInfoAttribute>();
                        if (pluginInfo != null && pluginInfo.SupportedVersion == GrandVersion.SupportedPluginVersion)
                        {
                            supportedVersion = true;
                            _fpath = entry.FullName[..entry.FullName.LastIndexOf("/", StringComparison.Ordinal)];
                            archive.Entries.Where(x => !x.FullName.Contains(_fpath)).ToList()
                                .ForEach(y => { archive.GetEntry(y.FullName)!.Delete(); });

                            _pluginInfo = new PluginInfo();
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                    }
                }

                if (!supportedVersion)
                    throw new Exception(
                        $"This plugin doesn't support the current version - {GrandVersion.SupportedPluginVersion}");

                var pluginname = _fpath[(_fpath.LastIndexOf('/') + 1)..];
                var _path = "";

                var entries = archive.Entries.ToArray();
                foreach (var y in entries)
                {
                    if (y.Name.Length > 0)
                        _path = y.FullName.Replace(y.Name, "").Replace(_fpath, pluginname).TrimEnd('/');
                    else
                        _path = y.FullName.Replace(_fpath, pluginname);

                    var _entry = archive.CreateEntry($"{_path}/{y.Name}");
                    using (var a = y.Open())
                    using (var b = _entry.Open())
                        a.CopyTo(b);

                    archive.GetEntry(y.FullName).Delete();
                }
            }

            if (_pluginInfo == null)
                throw new Exception("No info file is found.");

            if (string.IsNullOrEmpty(uploadedItemDirectoryName))
                throw new Exception($"Cannot get the plugin directory name");

            var pathToUpload = Path.Combine(pluginsDirectory, uploadedItemDirectoryName);

            try
            {
                if (Directory.Exists(pathToUpload))
                    DeleteDirectory(pathToUpload);
            }
            catch { }

            ZipFile.ExtractToDirectory(archivePath, pluginsDirectory);
        }

        #endregion
    }
}