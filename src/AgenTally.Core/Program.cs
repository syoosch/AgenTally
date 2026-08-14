using AgenTally.Core.Hosting;
using AgenTally.Storage;
using AgenTally.Storage.Runtime;

AgenTallyRuntimeProfile runtimeProfile = AgenTallyRuntimeProfile.CreateDefault();
var host = new CoreHost(
    new StorageOptions(runtimeProfile.DatabasePath),
    runtimeProfile: runtimeProfile);

return await host.RunAsync(args);
