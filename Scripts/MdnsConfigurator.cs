using Bindito.Core;

namespace RemoteApiAccess
{
    [Context("Game")]
    public class MdnsConfigurator : Configurator
    {
        protected override void Configure()
        {
            Bind<MdnsService>().AsSingleton();
        }
    }
}
