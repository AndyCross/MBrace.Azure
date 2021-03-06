﻿namespace MBrace.Azure.Runtime.Resources

open System
open System.Threading
open System.Runtime.Serialization
open MBrace.Azure.Runtime
open MBrace.Continuation
open MBrace.Azure.Runtime.Common
open Microsoft.WindowsAzure.Storage.Table

type DistributedCancellationTokenSource internal (config, res : Uri) = 
    let rec cancel table pk =
       async { 
            let! children = Table.queryPK<TableEntity> config table pk
            do! children 
                |> Seq.filter (fun e -> e.RowKey <> String.Empty)
                |> Seq.map (fun e -> cancel table e.RowKey)
                |> Async.Parallel
                |> Async.Ignore
            let e = new CancellationTokenSourceEntity(pk, IsCancellationRequested = true, ETag = "*")
            let! u = Table.merge config res.Table e
            return ()
        }

    let check() = 
        async { 
            let! e = Table.read<CancellationTokenSourceEntity> config res.Table res.PartitionWithScheme String.Empty
            return e.IsCancellationRequested
        }
    
    let cts = lazy new CancellationTokenSource()
    
    member __.Uri = res
    
    member __.IsCancellationRequested = check() |> Async.RunSync
    
    member __.Cancel() = Async.RunSync(__.CancelAsync())
    member __.CancelAsync() = cancel res.Table res.PartitionWithScheme
    
    member __.GetLocalCancellationToken() = 
        let rec loop () = async {
            let! isCancelled = check ()
            if isCancelled then
                cts.Value.Cancel()
            else
                do! Async.Sleep 200
                return! loop ()
        }

        Async.Start(loop())

        cts.Value.Token

    interface ISerializable with
        member x.GetObjectData(info: SerializationInfo, context: StreamingContext): unit = 
            info.AddValue("uri", res, typeof<Uri>)
            info.AddValue("config", config, typeof<ConfigurationId>)

    new(info: SerializationInfo, context: StreamingContext) =
        let res = info.GetValue("uri", typeof<Uri>) :?> Uri
        let config = info.GetValue("config", typeof<ConfigurationId>) :?> ConfigurationId
        new DistributedCancellationTokenSource(config, res)

    static member private GetUri(container, id) = uri "dcts:%s/%s" container id
    static member FromUri(config : ConfigurationId, uri) = new DistributedCancellationTokenSource(config, uri)
    static member Create(config, container : string, ?parent : DistributedCancellationTokenSource) = 
        async { 
            let childUri = DistributedCancellationTokenSource.GetUri(container, guid())
            let ctse = new CancellationTokenSourceEntity(childUri.PartitionWithScheme)         
            do! Table.insert<CancellationTokenSourceEntity> config childUri.Table ctse
            match parent with
            | None -> ()
            | Some p -> 
                let parentUri = p.Uri
                let link = new CancellationTokenLinkEntity(parentUri.PartitionWithScheme, childUri.PartitionWithScheme)
                do! Table.insert<CancellationTokenLinkEntity> config childUri.Table link
            let dcts = new DistributedCancellationTokenSource(config, childUri)
            match parent with
            | Some p when p.IsCancellationRequested -> dcts.Cancel()
            | _ -> ()
            return dcts
        }
