using System.Text;
using System.Collections.Generic;
using Pulumi;
using Pulumi.AzureNative.Resources;
using AC = Pulumi.AzureNative.ContainerService;
using ACI = Pulumi.AzureNative.ContainerService.Inputs;
using K8s = Pulumi.Kubernetes;
using Apps = Pulumi.Kubernetes.Apps.V1;
using Core = Pulumi.Kubernetes.Core.V1;
using AppsIn = Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using CoreIn = Pulumi.Kubernetes.Types.Inputs.Core.V1;
using MetaIn = Pulumi.Kubernetes.Types.Inputs.Meta.V1;

return await Pulumi.Deployment.RunAsync(() =>
{
    // --- Azure: Resource Group + AKS cluster ---------------------------------
    var rg = new ResourceGroup("myAKSClusterResourceGroup", new ResourceGroupArgs
    {
        ResourceGroupName = "my-aks-cluster-rg",
    });

    var cluster = new AC.ManagedCluster("myAKSCluster", new AC.ManagedClusterArgs
    {
        ResourceGroupName = rg.Name,
        ResourceName = "my-aks-cluster",
        KubernetesVersion = "1.33",
        DnsPrefix = "myaks",
        NodeResourceGroup = "my-aks-cluster-rg-nodes",
        EnableRBAC = true,
        Identity = new ACI.ManagedClusterIdentityArgs
        {
            Type = AC.ResourceIdentityType.SystemAssigned,
        },
        NetworkProfile = new ACI.ContainerServiceNetworkProfileArgs
        {
            NetworkDataplane = "cilium",
            NetworkPlugin = "azure",
            NetworkPluginMode = "overlay",
            NetworkPolicy = "cilium",
            PodCidr = "192.168.0.0/16",
        },
        AgentPoolProfiles =
        {
            new ACI.ManagedClusterAgentPoolProfileArgs
            {
                Name = "agentpool",
                Count = 3,
                VmSize = "Standard_B2ms",
                OsType = "Linux",
                OsDiskSizeGB = 30,
                Type = "VirtualMachineScaleSets",
                Mode = "System",
            },
        },
    });

    // --- Fetch the kubeconfig programmatically -------------------------------
    var creds = AC.ListManagedClusterUserCredentials.Invoke(new AC.ListManagedClusterUserCredentialsInvokeArgs
    {
        ResourceGroupName = rg.Name,
        ResourceName = cluster.Name,
    });

    var kubeconfig = creds.Apply(c =>
        Encoding.UTF8.GetString(System.Convert.FromBase64String(c.Kubeconfigs[0].Value)));

    // --- Programmatic Kubernetes provider ------------------------------------
    var k8sProvider = new K8s.Provider("myk8sProvider", new K8s.ProviderArgs
    {
        KubeConfig = Output.CreateSecret(kubeconfig),
        EnableServerSideApply = true,
    });
    var opts = new CustomResourceOptions { Provider = k8sProvider };

    var linuxSelector = new InputMap<string> { { "kubernetes.io/os", "linux" } };

    // --- RabbitMQ (message queue for orders) ---------------------------------
    // NB: original wired an enabled_plugins ConfigMap; dropped here as non-essential
    // (RabbitMQ serves AMQP on 5672 out of the box; store still functions).
    var rabbitmq = new Apps.Deployment("rabbitmq", new AppsIn.DeploymentArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "rabbitmq" },
        Spec = new AppsIn.DeploymentSpecArgs
        {
            Replicas = 1,
            Selector = new MetaIn.LabelSelectorArgs { MatchLabels = { { "app", "rabbitmq" } } },
            Template = new CoreIn.PodTemplateSpecArgs
            {
                Metadata = new MetaIn.ObjectMetaArgs { Labels = { { "app", "rabbitmq" } } },
                Spec = new CoreIn.PodSpecArgs
                {
                    NodeSelector = linuxSelector,
                    Containers =
                    {
                        new CoreIn.ContainerArgs
                        {
                            Name = "rabbitmq",
                            Image = "mcr.microsoft.com/mirror/docker/library/rabbitmq:3.10-management-alpine",
                            Ports =
                            {
                                new CoreIn.ContainerPortArgs { Name = "rabbitmq-amqp", ContainerPortValue = 5672 },
                                new CoreIn.ContainerPortArgs { Name = "rabbitmq-http", ContainerPortValue = 15672 },
                            },
                            Env =
                            {
                                new CoreIn.EnvVarArgs { Name = "RABBITMQ_DEFAULT_USER", Value = "username" },
                                new CoreIn.EnvVarArgs { Name = "RABBITMQ_DEFAULT_PASS", Value = "password" },
                            },
                        },
                    },
                },
            },
        },
    }, opts);

    var rabbitmqSvc = new Core.Service("rabbitmq", new CoreIn.ServiceArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "rabbitmq" },
        Spec = new CoreIn.ServiceSpecArgs
        {
            Type = "ClusterIP",
            Selector = { { "app", "rabbitmq" } },
            Ports =
            {
                new CoreIn.ServicePortArgs { Name = "rabbitmq-amqp", Port = 5672, TargetPort = 5672 },
                new CoreIn.ServicePortArgs { Name = "rabbitmq-http", Port = 15672, TargetPort = 15672 },
            },
        },
    }, opts);

    // --- Order service -------------------------------------------------------
    var orderService = new Apps.Deployment("order-service", new AppsIn.DeploymentArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "order-service" },
        Spec = new AppsIn.DeploymentSpecArgs
        {
            Replicas = 1,
            Selector = new MetaIn.LabelSelectorArgs { MatchLabels = { { "app", "order-service" } } },
            Template = new CoreIn.PodTemplateSpecArgs
            {
                Metadata = new MetaIn.ObjectMetaArgs { Labels = { { "app", "order-service" } } },
                Spec = new CoreIn.PodSpecArgs
                {
                    NodeSelector = linuxSelector,
                    InitContainers =
                    {
                        new CoreIn.ContainerArgs
                        {
                            Name = "wait-for-rabbitmq",
                            Image = "busybox",
                            Command = { "sh", "-c", "until nc -zv rabbitmq 5672; do echo waiting for rabbitmq; sleep 2; done;" },
                        },
                    },
                    Containers =
                    {
                        new CoreIn.ContainerArgs
                        {
                            Name = "order-service",
                            Image = "ghcr.io/azure-samples/aks-store-demo/order-service:latest",
                            Ports = { new CoreIn.ContainerPortArgs { ContainerPortValue = 3000 } },
                            Env =
                            {
                                new CoreIn.EnvVarArgs { Name = "ORDER_QUEUE_HOSTNAME", Value = "rabbitmq" },
                                new CoreIn.EnvVarArgs { Name = "ORDER_QUEUE_PORT", Value = "5672" },
                                new CoreIn.EnvVarArgs { Name = "ORDER_QUEUE_USERNAME", Value = "username" },
                                new CoreIn.EnvVarArgs { Name = "ORDER_QUEUE_PASSWORD", Value = "password" },
                                new CoreIn.EnvVarArgs { Name = "FASTIFY_ADDRESS", Value = "0.0.0.0" },
                            },
                        },
                    },
                },
            },
        },
    }, opts);

    var orderSvc = new Core.Service("order-service", new CoreIn.ServiceArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "order-service" },
        Spec = new CoreIn.ServiceSpecArgs
        {
            Type = "ClusterIP",
            Selector = { { "app", "order-service" } },
            Ports = { new CoreIn.ServicePortArgs { Name = "http", Port = 3000, TargetPort = 3000 } },
        },
    }, opts);

    // --- Product service -----------------------------------------------------
    var productService = new Apps.Deployment("product-service", new AppsIn.DeploymentArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "product-service" },
        Spec = new AppsIn.DeploymentSpecArgs
        {
            Replicas = 1,
            Selector = new MetaIn.LabelSelectorArgs { MatchLabels = { { "app", "product-service" } } },
            Template = new CoreIn.PodTemplateSpecArgs
            {
                Metadata = new MetaIn.ObjectMetaArgs { Labels = { { "app", "product-service" } } },
                Spec = new CoreIn.PodSpecArgs
                {
                    NodeSelector = linuxSelector,
                    Containers =
                    {
                        new CoreIn.ContainerArgs
                        {
                            Name = "product-service",
                            Image = "ghcr.io/azure-samples/aks-store-demo/product-service:latest",
                            Ports = { new CoreIn.ContainerPortArgs { ContainerPortValue = 3002 } },
                        },
                    },
                },
            },
        },
    }, opts);

    var productSvc = new Core.Service("product-service", new CoreIn.ServiceArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "product-service" },
        Spec = new CoreIn.ServiceSpecArgs
        {
            Type = "ClusterIP",
            Selector = { { "app", "product-service" } },
            Ports = { new CoreIn.ServicePortArgs { Name = "http", Port = 3002, TargetPort = 3002 } },
        },
    }, opts);

    // --- Store front (the public LoadBalancer) -------------------------------
    var storeFront = new Apps.Deployment("store-front", new AppsIn.DeploymentArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "store-front" },
        Spec = new AppsIn.DeploymentSpecArgs
        {
            Replicas = 1,
            Selector = new MetaIn.LabelSelectorArgs { MatchLabels = { { "app", "store-front" } } },
            Template = new CoreIn.PodTemplateSpecArgs
            {
                Metadata = new MetaIn.ObjectMetaArgs { Labels = { { "app", "store-front" } } },
                Spec = new CoreIn.PodSpecArgs
                {
                    NodeSelector = linuxSelector,
                    Containers =
                    {
                        new CoreIn.ContainerArgs
                        {
                            Name = "store-front",
                            Image = "ghcr.io/azure-samples/aks-store-demo/store-front:latest",
                            Ports = { new CoreIn.ContainerPortArgs { Name = "store-front", ContainerPortValue = 8080 } },
                            Env =
                            {
                                new CoreIn.EnvVarArgs { Name = "VUE_APP_ORDER_SERVICE_URL", Value = "http://order-service:3000/" },
                                new CoreIn.EnvVarArgs { Name = "VUE_APP_PRODUCT_SERVICE_URL", Value = "http://product-service:3002/" },
                            },
                        },
                    },
                },
            },
        },
    }, opts);

    var storeFrontSvc = new Core.Service("store-front", new CoreIn.ServiceArgs
    {
        Metadata = new MetaIn.ObjectMetaArgs { Name = "store-front" },
        Spec = new CoreIn.ServiceSpecArgs
        {
            Type = "LoadBalancer",
            Selector = { { "app", "store-front" } },
            Ports = { new CoreIn.ServicePortArgs { Port = 80, TargetPort = 8080 } },
        },
    }, opts);

    return new Dictionary<string, object?>
    {
        ["serviceIp"] = storeFrontSvc.Status.Apply(s => s.LoadBalancer.Ingress[0].Ip),
    };
});
