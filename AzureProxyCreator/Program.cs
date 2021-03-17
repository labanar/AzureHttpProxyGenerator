using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AzureProxyCreator
{
    public class Runner
    {
        private readonly IProxyService _proxyFactory;

        public Runner(IProxyService proxyFactory)
        {
            _proxyFactory = proxyFactory;
        }

        public async Task Run()
        {
            Console.WriteLine("Azure Proxy Creator by @labanar");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Available commands");
            Console.WriteLine("create {NUMBER_OF_PROXIES} {groupName} {region}");

            var processedCommand = false;
            while(!processedCommand)
            {
                var command = Console.ReadLine();
                processedCommand = await ProcessCommand(command);
            }
        }


        private async Task<bool> ProcessCommand(string command)
        {
            var canExitAfterProcessing = false;

            if (string.IsNullOrWhiteSpace(command))
                Console.WriteLine("Invalid command.");

            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "create": 
                    canExitAfterProcessing = await ProcessCreateCommand(command);
                    break;
                default:
                    Console.Write("Unrecognized command");
                    break;
            }

            return canExitAfterProcessing;
        }


        private async Task<bool> ProcessCreateCommand(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if(parts.Length != 4)
            {
                Console.WriteLine($"Invalid number of arguments, expecting 3 but got {parts.Length - 1}");
                return false;
            }

            if(!int.TryParse(parts[1], out var quantity))
            {
                Console.WriteLine($"Parsing error: {parts[1]} could not be converted to integer");
                return false;
            }
            var groupName = parts[2];
            var region = Region.Values.FirstOrDefault(x => x.Name.ToLower() == parts[3].ToLower());
            if (region == default)
            {
                Console.WriteLine($"Parsing error: {parts[3]} could not be converted to Region");
                return false;
            }

            var tasks = new List<Task<Proxy>>();
            for (var i = 0; i < quantity; i++)
            {
                var username = Guid.NewGuid().ToString("N");
                var password = Guid.NewGuid().ToString("N");
                tasks.Add(_proxyFactory.Create(username, password, region, groupName));
            }

            var proxies = await Task.WhenAll(tasks);
            Console.WriteLine(JsonConvert.SerializeObject(proxies));
            return true;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", true)
                .AddUserSecrets<Program>()
                .Build();

            var services = new ServiceCollection();
            services.Configure<AzureProxyServiceOptions>(config.GetSection("AzureProxyServiceOptions"));
            services.AddTransient<IProxyService, AzureProxyService>();
            services.AddTransient<Runner>();
            services.AddLogging(configure =>
            {
                configure.AddConsole();
            });

            var serviceProvider = services.BuildServiceProvider();
            var runner = serviceProvider.GetService<Runner>();
            await runner.Run();
        }
    }
}
