module RestApi
// implements chat API endpoints for Suave

open Akka.Actor
open Akkling
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl

open Suave
open Suave.Filters
open Suave.Logging
open Suave.RequestErrors
open Suave.WebSocket
open Suave.Operators

open ChannelFlow
open ChatServer
open SocketFlow

open FsChat
open ChatServer.Helpers

type private ServerActor = IActorRef<ChatServer.ServerControlMessage>
type Session = NoSession | UserLoggedOn of UserNick * ActorSystem * ServerActor

type IncomingMessage =
    | ChannelMessage of Uuid * Message
    | ControlMessage of Protocol.ServerMsg
    | Trash

module private Implementation =

    let encodeChannelMessage channel : UserNick ChatClientMessage -> Protocol.ClientMsg =
        // FIXME fill user info
        let userInfo (UserNick nickname) : Protocol.ChanUserInfo =
            {nick = nickname; name = "TBD"; email = None; online = true; isbot = false; lastSeen = System.DateTime.Now}

        function
        | ChatMessage ((id, ts),  UserNick author, Message message) ->
            Protocol.ChanMsg {id = id; ts = ts; text = message; chan = channel; author = author}
        | Joined ((id, ts), user, _) ->
            Protocol.UserJoined  ({id = id; ts = ts; user = userInfo user}, channel)
        | Left ((id, ts), user, _) ->
            Protocol.UserLeft  ({id = id; ts = ts; user = userInfo user}, channel)

    let logger = Log.create "chatapi"        

        // extracts message from websocket reply, only handles User input (channel * string)
    let extractMessage message =
        try
            match message with
            | Text t ->
                match t |> Json.unjson<Protocol.ServerMsg> with
                | Protocol.UserMessage msg ->
                    match Uuid.TryParse msg.chan with
                    | Some chanId -> ChannelMessage (chanId, Message msg.text)
                    | _ -> Trash
                | message -> ControlMessage message                
            |_ -> Trash
        with e ->
            do logger.error (Message.eventX "Failed to parse message '{msg}': {e}" >> Message.setFieldValue "msg" message  >> Message.setFieldValue "e" e)
            Trash
    let isChannelMessage = function
        | ChannelMessage (_,_) -> true | _ -> false
    let extractChannelMessage (ChannelMessage (chan, message)) = chan, message

    let isControlMessage = function
        | ControlMessage _ -> true | _ -> false
    let extractControlMessage (ControlMessage message) = message

    open ServerState

    let mapChanInfo (data: ChannelData) : Protocol.ChannelInfo =
        {id = data.id.ToString(); name = data.name; topic = data.topic; userCount = 0; users = []; joined = false}

    let setJoined v (ch: Protocol.ChannelInfo) =
        {ch with joined = v}

    let replyErrorProtocol requestId errtext =
        Protocol.CannotProcess (requestId, errtext) |> Protocol.ClientMsg.Error


    let connectWebSocket (system: ActorSystem) (server: ServerActor) me : WebPart =

        let materializer = system.Materializer()

        // session data
        let mutable session = UserSession.make server me
        let mutable listenChannel = None

        let updateSession requestId f =
            function
            | Ok (newSession, response) ->
                session <- newSession
                f response
            | Result.Error e ->
                replyErrorProtocol requestId e

        let reply requestId = function
            | Ok response ->    response
            | Result.Error e -> replyErrorProtocol requestId e

        let processControlMessage : Flow<Protocol.ServerMsg, Protocol.ClientMsg, _> =
            let processMsg (message: Protocol.ServerMsg) : Protocol.ClientMsg Async =
                async {
                    let requestId = "" // TODO take from server message
                    match message with

                    | Protocol.ServerMsg.Greets ->
                        let! channels = server |> (listChannels (fun _ -> true))

                        let makeChanInfo chanData =
                            { mapChanInfo chanData with joined = session.channels |> Map.containsKey chanData.id}
                        let (UserNick mynick) = session.me

                        let makeHello channels =
                            Protocol.ClientMsg.Hello {nick = mynick; name = ""; email = None; channels = channels}

                        return channels |> Result.map (List.map makeChanInfo >> makeHello) |> reply ""

                    | Protocol.ServerMsg.Join chanIdStr ->
                        match Uuid.TryParse chanIdStr with
                        | Some channelId ->
                            let! result = session |> UserSession.join listenChannel channelId
                            return result |> updateSession requestId (mapChanInfo >> (setJoined true) >> Protocol.JoinedChannel)
                        | _ -> return replyErrorProtocol requestId "bad channel id"

                    | Protocol.ServerMsg.JoinOrCreate channelName ->
                        let! channelResult = server |> getOrCreateChannel channelName
                        match channelResult with
                        | Ok channelData ->
                            let! result = session |> UserSession.join listenChannel channelData.id
                            return result |> updateSession requestId (mapChanInfo >> (setJoined true) >> Protocol.JoinedChannel)
                        | Result.Error err ->
                            return replyErrorProtocol requestId err

                    | Protocol.ServerMsg.Leave chanIdStr ->
                        return
                            Uuid.TryParse chanIdStr |> function
                            | Some channelId ->
                                let result = session |> UserSession.leave channelId
                                result |> updateSession requestId (fun _ -> Protocol.LeftChannel chanIdStr)
                            | _ ->
                                replyErrorProtocol requestId "bad channel id"

                    | _ ->
                        return replyErrorProtocol requestId "event was not processed"


                    // let! reply = server <? ServerMessage ("", me, message)
                    // return [reply]
                }
            Flow.empty<_, Akka.NotUsed> |> Flow.asyncMap 10 processMsg // |> Flow.collect id

        fun ctx -> async {
            // let server know websocket has gone (see onCompleteMessage)
            // FIXME not sure if it works at all
            let monitor t = async {
                logger.debug (Message.eventX "Monitor triggered for user {me}" >> Message.setFieldValue "me" me)
                let! _ = t
                match session |> UserSession.leaveAll with
                | Ok (newSession, _) -> session <- newSession
                | _ -> ()
            }

            let sessionFlow = createUserSessionFlow<UserNick,Uuid> materializer
            let userMessageFlow =
                Flow.empty<IncomingMessage, Akka.NotUsed>
                |> Flow.filter isChannelMessage
                |> Flow.map extractChannelMessage
                |> Flow.log "User flow"
                |> Flow.viaMat sessionFlow Keep.right
                |> Flow.map (fun (channel: Uuid, message) -> encodeChannelMessage (channel.ToString()) message)

            let controlFlow =
                Flow.empty<IncomingMessage, Akka.NotUsed>
                |> Flow.filter isControlMessage
                |> Flow.map extractControlMessage
                |> Flow.log "Control flow"
                |> Flow.via processControlMessage

            let combineFlow = FlowImpl.split2 userMessageFlow controlFlow Keep.left

            let socketFlow =
                Flow.empty<WsMessage, Akka.NotUsed>
                |> Flow.watchTermination (fun x t -> (monitor t) |> Async.Start; x)
                |> Flow.map extractMessage
                |> Flow.log "Extracting message"
                |> Flow.viaMat combineFlow Keep.right
                |> Flow.map (Json.json >> Text)

            let materialize materializer (source: Source<WsMessage, Akka.NotUsed>) (sink: Sink<WsMessage, _>) =
                listenChannel <-
                    source
                    |> Source.viaMat socketFlow Keep.right
                    |> Source.toMat sink Keep.left
                    |> Graph.run materializer |> Some

                ()

            let handler socket =
                handleWebsocketMessages system materialize socket
            return! handShake handler ctx
        }    

open Implementation

let api (session: Session): WebPart =
    match session with
    | UserLoggedOn (nick, actorSys, server) ->
        path "/api/socket" >=> (connectWebSocket actorSys server nick)
    | NoSession ->
        BAD_REQUEST "Authorization required"