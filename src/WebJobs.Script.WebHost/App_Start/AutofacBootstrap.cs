﻿using System;
using System.Configuration;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using Autofac;
using Microsoft.Azure.WebJobs.Script;
using WebJobs.Script.WebHost.WebHooks;

namespace WebJobs.Script.WebHost.App_Start
{
    public static class AutofacBootstrap
    {
        internal static void Initialize(ContainerBuilder builder)
        {
            string logFilePath;
            string scriptRootPath;
            string secretsPath;
            string home = Environment.GetEnvironmentVariable("HOME");
            bool isLocal = string.IsNullOrEmpty(home);
            if (isLocal)
            {
                // we're running locally
                scriptRootPath = Path.Combine(HostingEnvironment.ApplicationPhysicalPath, @"..\..\sample");
                logFilePath = Path.Combine(Path.GetTempPath(), @"Functions");
                secretsPath = HttpContext.Current.Server.MapPath("~/App_Data/Secrets");
            }
            else
            {
                // we're running in Azure
                scriptRootPath = Path.Combine(home, @"site\wwwroot");
                logFilePath = Path.Combine(home, @"LogFiles\Application\Functions");
                secretsPath = Path.Combine(home, @"data\Functions\secrets");
            }

            ScriptHostConfiguration scriptHostConfig = new ScriptHostConfiguration()
            {
                RootScriptPath = scriptRootPath,
                RootLogPath = logFilePath,
                FileLoggingEnabled = true
            };

            // If there is an explicit machine key, it makes a good default host id. It can still be
            // overridden in host.json
            var section = (MachineKeySection)ConfigurationManager.GetSection("system.web/machineKey");
            if (section.Decryption != "Auto" && section.ValidationKey.Length >= 32)
            {
                scriptHostConfig.HostConfig.HostId = section.ValidationKey.Substring(0, 32).ToLowerInvariant();
            }

            WebScriptHostManager scriptHostManager = new WebScriptHostManager(scriptHostConfig);
            builder.RegisterInstance<WebScriptHostManager>(scriptHostManager);

            SecretManager secretManager = new SecretManager(secretsPath);
            builder.RegisterInstance<SecretManager>(secretManager);

            WebHookReceiverManager webHookRecieverManager = new WebHookReceiverManager(secretManager);
            builder.RegisterInstance<WebHookReceiverManager>(webHookRecieverManager);

            HostingEnvironment.QueueBackgroundWorkItem((ct) => scriptHostManager.RunAndBlock(ct));
        }
    }
}