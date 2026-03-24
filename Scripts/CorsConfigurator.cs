using Bindito.Core;
using Timberborn.HttpApiSystem;

namespace RemoteApiAccess
{
    [Context("Game")]
    public class CorsConfigurator : Configurator
    {
        protected override void Configure()
        {
            MultiBind<IHttpApiEndpoint>().To<CorsPreflightEndpoint>().AsSingleton();
        }
    }
}