using System;
using System.Threading.Tasks;
using Terradue.Stars.Interface.Router;
using Terradue.Stars.Interface.Supplier;
using Terradue.Stars.Interface.Supplier.Destination;
using Terradue.Stars.Services.Supplier.Carrier;
using Microsoft.Extensions.Configuration;
using Terradue.Stars.Interface;
using Terradue.Stars.Services.Plugins;
using System.Threading;

namespace Terradue.Stars.Services.Supplier
{
    [PluginPriority(10)]
    public class NativeSupplier : ISupplier
    {
        private readonly CarrierManager carriersManager;

        public NativeSupplier(CarrierManager carriersManager)
        {
            this.carriersManager = carriersManager;
        }

        public int Priority { get; set; }
        public string Key { get => Id; set {} }

        public string Id => "Native";

        public string Label => "Native Supplier (self resource)";

        public Task<IResource> SearchForAsync(IResource resource, CancellationToken ct, string identifierRegex = null)
        {
            return Task.FromResult<IResource>(resource);
        }

        public Task<IOrder> Order(IOrderable orderableRoute)
        {
            throw new NotSupportedException();
        }
    }
}
