using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Network.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzureProxyCreator
{
    public class AzureProxyServiceOptions
    {
        public string SubscriptionId { get; set; }
        public string TenantId { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
    }

    public class AzureProxyService : IProxyService
    {
        private readonly AzureProxyServiceOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public AzureProxyService(IOptions<AzureProxyServiceOptions> options, ILoggerFactory loggerFactory)
        {
            _options = options.Value;
            _loggerFactory = loggerFactory;
        }

        private Func<string, string> ResourceName = (proxyId) => $"Proxy-{proxyId}";

        public async Task<Proxy> Create(string username, string password, Region region, string groupName)
        {
            var proxyId = Guid.NewGuid().ToString("N");
            var logger =  _loggerFactory.CreateLogger($"{typeof(AzureProxyService).FullName}.{proxyId}");

            var creds = new AzureCredentialsFactory()
                .FromServicePrincipal(
                    _options.ClientId,
                    _options.ClientSecret,
                    _options.TenantId,
                    AzureEnvironment.AzureGlobalCloud);

            var azure = Azure
                .Authenticate(creds)
                .WithSubscription(_options.SubscriptionId);

            logger.LogInformation("Creating Resource Group...");
            var resourceGroup = await azure.ResourceGroups.Define(ResourceName(proxyId))
                .WithRegion(region)
                .WithTag("Group", groupName)
                .CreateAsync();
            logger.LogInformation("Resource Group Created!");

            logger.LogInformation("Creating Virtual Network...");
            var virtualNetwork = await azure.Networks.Define(ResourceName(proxyId))
                .WithRegion(resourceGroup.Region)
                .WithExistingResourceGroup(resourceGroup)
                .WithAddressSpace("10.0.2.0/24")
                .WithSubnets(new Dictionary<string, string>
                {
                    { "default", "10.0.2.0/24" }
                })
                .CreateAsync();
            logger.LogInformation("Virtual Network Created!");

            logger.LogInformation("Creating Public IP Address...");
            var publicIpAddress = await azure.PublicIPAddresses.Define(ResourceName(proxyId))
                .WithRegion(resourceGroup.Region)
                .WithExistingResourceGroup(resourceGroup)
                .WithSku(PublicIPSkuType.Basic)
                .WithStaticIP()
                .CreateAsync();
            logger.LogInformation("Public IP Address Created!");

            logger.LogInformation("Creating Network Security Group...");
            var networkSecurityGroup = await azure.NetworkSecurityGroups.Define(ResourceName(proxyId))
                .WithRegion(resourceGroup.Region)
                .WithExistingResourceGroup(resourceGroup)
                .DefineRule("SSH")
                    .AllowInbound()
                    .FromAnyAddress()
                    .FromAnyPort()
                    .ToAnyAddress()
                    .ToPort(22)
                    .WithProtocol(SecurityRuleProtocol.Tcp)
                    .WithPriority(300)
                    .Attach()
                .DefineRule("ProxyPort")
                    .AllowInbound()
                    .FromAnyAddress()
                    .FromAnyPort()
                    .ToAnyAddress()
                    .ToPort(3128)
                    .WithAnyProtocol()
                    .WithPriority(310)
                    .Attach()
                .CreateAsync();
            logger.LogInformation("Network Security Group Created!");

            logger.LogInformation("Creating Network Interface...");
            var networkInterface = await azure.NetworkInterfaces.Define(ResourceName(proxyId))
                .WithRegion(resourceGroup.Region)
                .WithExistingResourceGroup(resourceGroup)
                .WithExistingPrimaryNetwork(virtualNetwork)
                .WithSubnet("default")
                .WithPrimaryPrivateIPAddressDynamic()
                .WithExistingPrimaryPublicIPAddress(publicIpAddress)
                .WithExistingNetworkSecurityGroup(networkSecurityGroup)
                .CreateAsync();
            logger.LogInformation("Network Interface Created!");

            logger.LogInformation("Creating Virtual Machine...");
            var vmUsername = Guid.NewGuid().ToString("N");
            var vmPassword = Guid.NewGuid().ToString("N") + "!@123";
            var proxyVm = await azure.VirtualMachines.Define(ResourceName(proxyId))
                .WithRegion(resourceGroup.Region)
                .WithExistingResourceGroup(resourceGroup)
                .WithExistingPrimaryNetworkInterface(networkInterface)
                .WithLatestLinuxImage("debian", "debian-10", "10")
                .WithRootUsername(vmUsername)
                .WithRootPassword(vmPassword)
                .WithSize("Standard_B1ls")
                .CreateAsync();
            logger.LogInformation("Virtual Machine Created!");


            logger.LogInformation("Waiting 1m before performing proxy config");
            await Task.Delay(TimeSpan.FromMinutes(1));
            logger.LogInformation("Configuring proxy");

            using (var client = new SshClient(publicIpAddress.IPAddress, vmUsername, vmPassword))
            {
                client.Connect();
                logger.LogInformation("Running apt-get update and apt-get upgrade");
                client.RunCommand("sudo apt-get update && sudo apt-get upgrade -y");

                logger.LogInformation("Installing squid proxy");
                client.RunCommand("sudo apt-get install squid -y");

                logger.LogInformation("Installing apche2-utils");
                client.RunCommand("sudo apt-get install apache2-utils -y");

                logger.LogInformation("Backing up default squid.conf");
                client.RunCommand("sudo cp /etc/squid/squid.conf /etc/squid/squid.conf.default");

                logger.LogInformation("Deleting existing squid config");
                client.RunCommand("sudo rm /etc/squid/squid.conf");

                logger.LogInformation("Creating squid password file");
                client.RunCommand("sudo touch /etc/squid/squid_passwd");

                logger.LogInformation("Setting ownership on squid password file");
                client.RunCommand("sudo chown proxy /etc/squid/squid_passwd");

                logger.LogInformation("Adding username and password to squid password file");
                client.RunCommand($"sudo htpasswd -b /etc/squid/squid_passwd {username} {password}");

                logger.LogInformation("Creating squid.conf");
                client.RunCommand("echo \"auth_param basic program /usr/lib/squid/basic_ncsa_auth /etc/squid/squid_passwd\" | sudo tee /etc/squid/squid.conf");
                client.RunCommand("echo \"acl ncsa_users proxy_auth REQUIRED\" | sudo tee -a /etc/squid/squid.conf");
                client.RunCommand("echo \"http_access allow ncsa_users\" | sudo tee -a /etc/squid/squid.conf");
                client.RunCommand("echo \"http_port 3128\" | sudo tee -a /etc/squid/squid.conf");

                logger.LogInformation("Restarting squid service");
                client.RunCommand("sudo systemctl restart squid");
            }


            logger.LogInformation("Proxy Created!");

            return new Proxy
            {
                Host = publicIpAddress.IPAddress,
                Port = 3128,
                Username = username,
                Password = password
            };
        }
    }
}
