﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modular.WebHost.Data;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Mvc.Razor;
using Modular.WebHost.Extensions;
using System.IO;
using Modular.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;
using Modular.Modules.Core.Models;
using Modular.Modules.Core.Services;

namespace Modular.WebHost
{
    public class Startup
    {
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly IList<ModuleInfo> modules = new List<ModuleInfo>();

        public Startup(IHostingEnvironment env)
        {
            _hostingEnvironment = env;

            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true);

            if (env.IsDevelopment())
            {
                // For more details on using the user secret store see http://go.microsoft.com/fwlink/?LinkID=532709
                builder.AddUserSecrets();
            }

            builder.AddEnvironmentVariables();
            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add framework services.
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));

            services.AddIdentity<ApplicationUser, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationExpanders.Add(new ModuleViewLocationExpander());
            });

            var moduleRootFolder = _hostingEnvironment.ContentRootFileProvider.GetDirectoryContents("/Modules");
            var moduleAssemblies = new List<Assembly>();
            foreach(var moduleFolder in moduleRootFolder.Where(x => x.IsDirectory))
            {
                modules.Add(new ModuleInfo { Name = moduleFolder.Name, Path = moduleFolder.PhysicalPath });

                var binFolder = new DirectoryInfo(Path.Combine(moduleFolder.PhysicalPath, "bin"));
                if (!binFolder.Exists)
                {
                    continue;
                }

                foreach(var file in binFolder.GetFileSystemInfos("*.dll", SearchOption.AllDirectories))
                {
                    // If the assemblies are referenced by the Host, then this will throw exception
                    try
                    {
                        moduleAssemblies.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(file.FullName));
                    }
                    catch { }
                }
            }

            var mvcBuilder = services.AddMvc();
            var moduleInitializerInterface = typeof(IModuleInitializer);
            foreach(var assembly in moduleAssemblies)
            {
                // Register controller from modules
                mvcBuilder.AddApplicationPart(assembly);

                // Register dependency in modules
                var moduleInitializerType = assembly.GetTypes().Where(x => typeof(IModuleInitializer).IsAssignableFrom(x)).FirstOrDefault();
                if(moduleInitializerType != null)
                {
                    var moduleInitializer = (IModuleInitializer)Activator.CreateInstance(moduleInitializerType);
                    moduleInitializer.Init(services);
                }
            }

            // Add application services.
            services.AddTransient<IEmailSender, AuthMessageSender>();
            services.AddTransient<ISmsSender, AuthMessageSender>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseDatabaseErrorPage();
                app.UseBrowserLink();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseStaticFiles();

            // Serving static file for modules
            foreach(var module in modules)
            {
                var wwwrootDir = new DirectoryInfo(Path.Combine(module.Path, "wwwroot"));
                if (!wwwrootDir.Exists)
                {
                    continue;
                }

                app.UseStaticFiles(new StaticFileOptions()
                {
                    FileProvider = new PhysicalFileProvider(wwwrootDir.FullName),
                    RequestPath = new PathString("/"+ module.SortName)
                });
            }

            app.UseIdentity();

            // Add external authentication middleware below. To configure them please see http://go.microsoft.com/fwlink/?LinkID=532715

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
        }
    }
}
