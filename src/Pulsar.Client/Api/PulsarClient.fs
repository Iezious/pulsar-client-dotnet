﻿namespace Pulsar.Client.Api

open FSharp.Control.Tasks.V2.ContextInsensitive
open Pulsar.Client.Internal
open Microsoft.Extensions.Logging
open System.Collections.Generic
open System.Threading.Tasks
open Pulsar.Client.Common
open System.Threading

type PulsarClientState =
    | Active
    | Closing
    | Closed

type PulsarClientMessage =
    | RemoveProducer of Producer
    | RemoveConsumer of Consumer
    | AddProducer of Producer
    | AddConsumer of Consumer
    | Close of AsyncReplyChannel<Task>
    | Stop

type PulsarClient(config: PulsarClientConfiguration) as this =

    let connectionPool = ConnectionPool(config)
    let lookupSerivce = BinaryLookupService(config, connectionPool)
    let producers = HashSet<Producer>()
    let consumers = HashSet<Consumer>()
    let mutable clientState = Active

    let tryStopMailbox() =
        match this.ClientState with
        | Closing ->
            if consumers.Count = 0 && producers.Count = 0 then
                this.Mb.Post(Stop)
        | _ ->
            ()

    let checkIfActive() =
        match this.ClientState with
        | Active ->  ()
        | _ ->  raise <| AlreadyClosedException("Client already closed. State: " + this.ClientState.ToString())

    let mb = MailboxProcessor<PulsarClientMessage>.Start(fun inbox ->

        let rec loop () =
            async {
                let! msg = inbox.Receive()
                match msg with
                | RemoveProducer producer ->
                    producers.Remove(producer) |> ignore
                    tryStopMailbox()
                    return! loop ()
                | RemoveConsumer consumer ->
                    consumers.Remove(consumer) |> ignore
                    tryStopMailbox ()
                    return! loop ()
                | AddProducer producer ->
                    producers.Add producer |> ignore
                    return! loop ()
                | AddConsumer consumer ->
                    consumers.Add consumer |> ignore
                    return! loop ()
                | Close channel ->
                    match this.ClientState with
                    | Active ->
                        Log.Logger.LogInformation("Client closing. URL: {}", lookupSerivce.GetServiceUrl())
                        this.ClientState <- Closing
                        let producersTasks = producers |> Seq.map (fun producer -> producer.CloseAsync())
                        let consumerTasks = consumers |> Seq.map (fun consumer -> consumer.CloseAsync())
                        task {
                            try
                                let! _ = Task.WhenAll (seq { yield! producersTasks; yield! consumerTasks })
                                tryStopMailbox()
                            with ex ->
                                Log.Logger.LogError(ex, "Couldn't stop client")
                                this.ClientState <- Active
                        } |> channel.Reply
                        return! loop ()
                    | _ ->
                        channel.Reply(Task.FromException(AlreadyClosedException("Client already closed. URL: " + lookupSerivce.GetServiceUrl())))
                        return! loop ()
                | Stop ->
                    this.ClientState <- Closed
                    connectionPool.Close()
                    Log.Logger.LogInformation("Pulsar client stopped")
            }
        loop ()
    )

    do mb.Error.Add(fun ex -> Log.Logger.LogCritical(ex, "PulsarClient mailbox failure"))

    static member Logger
        with get () = Log.Logger
        and set (value) = Log.Logger <- value

    member this.SubscribeAsync consumerConfig =
        task {
            checkIfActive()
            return! this.SingleTopicSubscribeAsync consumerConfig
        }

    member this.GetPartitionedTopicMetadata topicName =
        task {
            checkIfActive()
            return! lookupSerivce.GetPartitionedTopicMetadata topicName
        }

    member this.CloseAsync() =
        task {
            checkIfActive()
            let! t = mb.PostAndAsyncReply(Close)
            return! t
        }

    member private this.SingleTopicSubscribeAsync (consumerConfig: ConsumerConfiguration) =
        task {
            checkIfActive()
            Log.Logger.LogDebug("SingleTopicSubscribeAsync started")
            let! metadata = this.GetPartitionedTopicMetadata consumerConfig.Topic.CompleteTopicName
            let removeConsumer = fun consumer -> mb.Post(RemoveConsumer consumer)
            if (metadata.Partitions > 1u)
            then
                let! consumer = Consumer.Init(consumerConfig, config, connectionPool, SubscriptionMode.Durable, lookupSerivce, removeConsumer)
                consumers.Add(consumer) |> ignore
                return consumer
            else
                let! consumer = Consumer.Init(consumerConfig, config, connectionPool, SubscriptionMode.Durable, lookupSerivce, removeConsumer)
                consumers.Add(consumer) |> ignore
                return consumer
        }

    member this.CreateProducerAsync (producerConfig: ProducerConfiguration) =
        task {
            checkIfActive()
            Log.Logger.LogDebug("CreateProducerAsync started")
            let! metadata = this.GetPartitionedTopicMetadata producerConfig.Topic.CompleteTopicName
            let removeProducer = fun producer -> mb.Post(RemoveProducer producer)
            if (metadata.Partitions > 0u) then
                let! producer = Producer.Init(producerConfig, config, connectionPool, lookupSerivce, removeProducer)
                producers.Add(producer) |> ignore
                return producer
            else
                let! producer = Producer.Init(producerConfig, config, connectionPool, lookupSerivce, removeProducer)
                producers.Add(producer) |> ignore
                return producer
        }

    member private this.Mb with get(): MailboxProcessor<PulsarClientMessage> = mb

    member private this.ClientState
        with get() = Volatile.Read(&clientState)
        and set(value) = Volatile.Write(&clientState, value)