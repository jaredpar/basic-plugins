using Azure.Identity;

namespace Pipeline.Core;

public static class PipelineUtils
{
    public static DefaultAzureCredential CreateCredential() =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions()
        {
            TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        });
}
