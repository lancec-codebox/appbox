// IndexNode GPU node as an Azure Container Apps event-triggered job (NOT deployed by this repo: review, then deploy yourself).
//
//   az deployment group create -g rg-lancec-oss -f indexnode/deploy/gpu-job.bicep \
//     -p environmentId=<id of the ACA environment that has the A100 workload profile> workloadProfileName=<profile name> \
//        image=ghcr.io/<owner>/indexnode-gpu:0.6.0 sasUrl='<container SAS URL>' blobConnectionString='<see below>'
//
// Trigger: the KEDA azure-blob scaler counts the blobs under index/gpu-ready/ (written by the frames step when a video's frames are done);
// one execution starts per scale poll while the count is at least blobCount, up to parallelism. The job runs
//   indexnode run --gpu --node-name gpu-a100-westus3 --exit-when-idle 10m
// takes sscd, clip and cast work for videos whose frames are done, deletes each marker when its job finishes, and exits after 10 idle minutes.
// A CPU node takes a GPU-stage job only after it has waited 30 minutes, so nothing stalls while the A100 is off.

param location string = resourceGroup().location
param jobName string = 'indexnode-gpu'
@description('Resource id of the Container Apps environment that has the GPU (A100) workload profile.')
param environmentId string
@description('Name of the A100 workload profile in that environment.')
param workloadProfileName string
param image string = 'ghcr.io/ref12labs/indexnode-gpu:latest'
@description('GHCR pull credentials; leave empty when the image is public.')
param registryUser string = ''
@secure()
param registryPassword string = ''
@description('The Blob container SAS URL IndexNode uses (INDEXNODE_SAS_URL).')
@secure()
param sasUrl string
@description('Connection string for the KEDA scaler. A SAS works: BlobEndpoint=https://<account>.blob.core.windows.net/;SharedAccessSignature=<sas token>. It needs list (l) and read (r) on the container.')
@secure()
param blobConnectionString string
param blobContainerName string = 'media-wus2'
@description('Blobs under this prefix that start an execution (one marker per video step).')
param markerPrefix string = 'index/gpu-ready/'
param blobCount string = '1'
param pollingIntervalSeconds int = 30
@description('Executions running at once. One A100 serves one model step at a time, so 1 is normal; 2 lets sscd and clip overlap.')
param maxExecutions int = 1
param cpu string = '8'
param memory string = '56Gi'
param nodeName string = 'gpu-a100-westus3'
param exitWhenIdle string = '10m'
@description('Longest one execution may run, seconds (default 6 h).')
param replicaTimeout int = 21600

var useRegistry = !empty(registryUser)

resource job 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: jobName
  location: location
  properties: {
    environmentId: environmentId
    workloadProfileName: workloadProfileName
    configuration: {
      triggerType: 'Event'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      eventTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
        scale: {
          minExecutions: 0
          maxExecutions: maxExecutions
          pollingInterval: pollingIntervalSeconds
          rules: [
            {
              name: 'gpu-ready-markers'
              type: 'azure-blob'
              metadata: {
                containerName: blobContainerName
                blobPrefix: markerPrefix
                blobCount: blobCount
              }
              auth: [
                {
                  secretRef: 'blob-connection'
                  triggerParameter: 'connection'
                }
              ]
            }
          ]
        }
      }
      secrets: concat([
        { name: 'blob-connection', value: blobConnectionString }
        { name: 'indexnode-sas', value: sasUrl }
      ], useRegistry ? [ { name: 'registry-password', value: registryPassword } ] : [])
      registries: useRegistry ? [
        { server: 'ghcr.io', username: registryUser, passwordSecretRef: 'registry-password' }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'indexnode-gpu'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          args: [ 'run', '--gpu', '--node-name', nodeName, '--exit-when-idle', exitWhenIdle ]
          env: [
            { name: 'INDEXNODE_SAS_URL', secretRef: 'indexnode-sas' }
            { name: 'INDEXNODE_REGION', value: 'westus3' }
          ]
        }
      ]
    }
  }
}

output jobId string = job.id
