using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using System.Threading.Tasks;

namespace AzureProxyCreator
{
    public interface IProxyService 
    {
        Task<Proxy> Create(string username, string password, Region region, string groupName);
    }
}
