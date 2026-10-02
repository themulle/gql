namespace GqlGateway.Application.DataCatalog.Interfaces;

using GqlGateway.Domain.Common;

public interface IDataCatalogClientFactory
{
    IDataCatalogClient CreateClient(DataCatalogProviderType providerType);
    IDataCatalogClient GetActiveClient();
}
