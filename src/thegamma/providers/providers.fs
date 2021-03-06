﻿namespace TheGamma.TypeProviders

open TheGamma
open TheGamma.Babel
open TheGamma.Common
open Fable.Import

type ProvidedType = 
  | NamedType of name:string * typars:string list * typ:Type
  | GlobalValue of string * Metadata list * Expression * Type

// ------------------------------------------------------------------------------------------------
// F# provider
// ------------------------------------------------------------------------------------------------

module ProviderHelpers = 
  let docMeta doc = 
    { Context = "http://thegamma.net"
      Type = "Documentation"
      Data = box doc }

open ProviderHelpers

module FSharpProvider = 
  type AnyType = 
    { kind : string }

  type GenericParameterType = 
    { kind : string 
      name : string }

  type ArrayType = 
    { kind : string 
      element : AnyType }

  type PrimitiveType = 
    { kind : string 
      name : string }

  type FunctionType = 
    { kind : string 
      arguments : AnyType[]
      returns : AnyType }

  type NamedType = 
    { kind : string 
      name : string
      typargs : AnyType[] }
  
  type Member = 
    { kind : string }

  type Argument = 
    { name : string
      optional : bool
      ``type`` : AnyType }

  type MethodMember = 
    { kind : string
      name : string 
      typepars : AnyType[]
      arguments:Argument[]
      returns : AnyType }

  type PropertyMember = 
    { kind : string
      name : string 
      returns : AnyType }

  type ExportedType = 
    { name : string
      typepars : AnyType[]
      ``static`` : bool 
      instance : string[]
      members : Member[] }

  let provideFSharpTypes lookupNamed url = 

    let rec mapType (t:AnyType) = 
      match t.kind with
      | "primitive" -> 
          match (unbox<PrimitiveType> t).name with
          | "object" -> Type.Any 
          | "int" | "float" -> Type.Primitive PrimitiveType.Number
          | "string" -> Type.Primitive PrimitiveType.String
          | "bool" -> Type.Primitive PrimitiveType.Bool
          | "unit" -> Type.Primitive PrimitiveType.Unit
          | t -> failwith ("provideFSharpType: Unsupported type: " + t)
      | "function"->
          let t = unbox<FunctionType> t
          Type.Function(List.ofSeq (Array.map mapType t.arguments),mapType t.returns)
      | "named" -> 
          let t = (unbox<NamedType> t)
          lookupNamed t.name (List.ofArray (Array.map mapType t.typargs))
      | "parameter" -> Type.Parameter (unbox<GenericParameterType> t).name
      | "array" -> Type.List(mapType (unbox<ArrayType> t).element)
      | _ -> failwith "provideFSharpType: Unexpected type"

    let getTypeParameters typars = 
      typars |> Array.map (fun t -> 
        match mapType t with
        | Type.Parameter(n) -> n
        | _ -> failwith "importProvidedType: expected type parameter") |> List.ofArray

    // Needs to be delayed to avoid calling lookupNamed too early
    let importProvidedType exp = async {
      let mems = 
        exp.members |> Array.choose (fun m ->
          if m.kind = "method" then
            let m = unbox<MethodMember> m
            let args = [ for a in m.arguments -> a.name, a.optional, mapType a.``type`` ]
            let emitter = { Emit = fun (inst, args) ->
              CallExpression
                ( MemberExpression(inst, IdentifierExpression(m.name, None), false, None), 
                  args, None) }
            
            let typ = 
              match getTypeParameters m.typepars with
              | [] -> mapType m.returns
              | pars -> Type.Forall(pars, mapType m.returns)

            Some(Member.Method(m.name, args, typ, [docMeta (Documentation.Text "")], emitter))
          else None)

      match getTypeParameters exp.typepars with
      | [] -> return Type.Object { Members = mems }
      | typars ->
          let obj = Type.Object { Members = mems }
          return Type.Forall(typars, obj) } |> Async.CreateNamedFuture exp.name
            
    async {
      let! json = Http.Request("GET", url)
      let expTys = jsonParse<ExportedType[]> json
      return
        [ for exp in expTys ->
            let guid = url + "," + exp.name
            let ty = Type.Delayed(guid, importProvidedType exp)
            if exp.``static`` then           
              let e = exp.instance |> Seq.fold (fun chain s -> 
                match chain with
                | None -> Some(IdentifierExpression(s, None))
                | Some e -> Some(MemberExpression(e, IdentifierExpression(s, None), false, None)) ) None |> Option.get
              let ty = 
                match getTypeParameters exp.typepars with 
                | [] -> ty
                | tya -> Type.App(ty, [for v in tya -> Type.Any])
              ProvidedType.GlobalValue(exp.name, [], e, ty)
            else
              ProvidedType.NamedType(exp.name, getTypeParameters exp.typepars, ty) ] }
    

// ------------------------------------------------------------------------------------------------
// REST provider
// ------------------------------------------------------------------------------------------------

module RestProvider = 

  type AnyType = { kind:string }
  type TypeNested = { kind:string (* = nested *); endpoint:string }
  type TypePrimitive = { kind:string (* = primitive *); ``type``:obj; endpoint:string }

  [<Fable.Core.Emit("typeof($0)")>]
  let jstypeof (o:obj) : string = failwith "!"

  type Parameter = 
    { name : string 
      ``type`` : string }

  type Documentation = 
    { title : string option
      details : string option }

  type Member =
    { name : string
      returns : AnyType
      parameters : Parameter[] option
      documentation : obj option // This can be Documentation or string or an endpoint
      schema : obj option
      trace : string[] }

  type ResultType = 
    | Primitive of string
    | Generic of string * ResultType[]
    | Record of (string * ResultType)[]

  type RawField = 
    { name : string
      ``type`` : obj }

  type RawResultType = 
    { name : string 
      fields : RawField[]
      ``params`` : obj[] }

  let parseDoc (json:obj option) =
    if json.IsNone then Documentation.None
    elif jstypeof json.Value = "string" then Documentation.Text(unbox json)
    else 
      let doc = unbox<Documentation> json.Value
      match doc.title, doc.details with 
      | Some title, Some dets -> Documentation.Details(title, dets)
      | _ -> Documentation.None

  let rec fromRawType (json:obj) =
    if jstypeof json = "string" then Primitive(unbox json)
    else
      let res = unbox<RawResultType> json
      if res.name = "record" then res.fields |> Array.map (fun f -> f.name, fromRawType f.``type``) |> Record
      else Generic(res.name, res.``params`` |> Array.map fromRawType)
 
  let load url cookies = async {
    let! json = Http.Request("GET", url, cookies=cookies)
    let members = jsonParse<Member[]> json
    return members }

  let trimLeft c (s:string) = s.ToCharArray() |> Array.skipWhile ((=) c) |> System.String
  let trimRight c (s:string) = s.ToCharArray() |> Array.rev |> Array.skipWhile ((=) c) |> Array.rev |> System.String

  let concatUrl (a:string) (b:string) =
    (trimRight '/' a) + "/" + (trimLeft '/' b)

  let addTraceCall inst trace =
    if Seq.isEmpty trace then inst 
    else
      let trace = StringLiteral(String.concat "&" trace, None)    
      let mem = MemberExpression(inst, IdentifierExpression("addTrace", None), false, None)
      CallExpression(mem, [trace], None)

  let propAccess trace = 
    { Emit = fun (inst, _args) -> addTraceCall inst trace }

  let methCall argNames trace =
    { Emit = fun (inst, args) ->
        let withTrace = addTraceCall inst trace
        Seq.zip argNames args |> Seq.fold (fun inst (name, value) ->
          let trace = BinaryExpression(BinaryPlus, StringLiteral(name + "=", None), value, None)
          let mem = MemberExpression(inst, IdentifierExpression("addTrace", None), false, None)
          CallExpression(mem, [trace], None) ) withTrace }

  let dataCall parser trace endp = 
    { Emit = fun (inst, args) ->
        let tr = (propAccess trace).Emit(inst, args) 
        let mem = MemberExpression(tr, IdentifierExpression("getValue", None), false, None)
        CallExpression(mem, [StringLiteral(endp, None)], None) |> parser }

  let ident s = IdentifierExpression(s, None)
  let str v = StringLiteral(v, None)
  let (?) (e:Expression) (s:string) = MemberExpression(e, IdentifierExpression(s, None), false, None)
  let (/@/) (e:Expression) (args) = CallExpression(e, args, None)
  let func v f = 
    let body = BlockStatement([ReturnStatement(f (ident v), None)], None)
    FunctionExpression(None, [IdentifierPattern(v, None)], body, false, false, None)


  // Turn "Async<string>" into the required type
  // I guess we should keep a flag whether the input is still async (or something)
  let rec getTypeAndEmitter (lookupNamed:string -> TheGamma.Type list -> TheGamma.Type) ty = 
    match ty with
    | Primitive("string") -> Type.Primitive(PrimitiveType.String), id
    | Primitive("int") 
    | Primitive("float") -> 
        Type.Primitive(PrimitiveType.Number), 
        fun e -> CallExpression(IdentifierExpression("Number", None), [e], None)
    | Generic("seq", [|Generic("tuple", [|t1; t2|])|]) -> 
        let t1, e1 = getTypeAndEmitter lookupNamed t1
        let t2, e2 = getTypeAndEmitter lookupNamed t2
        let typ = lookupNamed "series" [t1; t2]
        typ, 
        fun d -> 
          ident("series")?create /@/ 
            [ ident("convertTupleSequence") /@/ [func "v" e1; func "v" e2; d] 
              str "key"; str "value"; str "" ] // TODO: We don't have any info - that sucks
    | Generic("seq", [|ty|]) ->
        let elTy, emitter = getTypeAndEmitter lookupNamed ty
        let serTy = lookupNamed "series" [Type.Primitive PrimitiveType.Number; elTy]
        serTy, 
        // This is over async, but the child `emitter` is not over async
        fun d -> 
          ident("series")?ordinal /@/ 
            [ ident("convertSequence") /@/ [func "v" emitter; d] 
              str "key"; str "value"; str "" ]
    | Record(membs) ->
        let membs = 
          membs |> Array.map (fun (name, ty) ->
            let memTy, memConv = getTypeAndEmitter lookupNamed ty
            let emitter = { Emit = fun (inst, _) -> memConv <| inst?(name) }
            Member.Property(name, memTy, [docMeta(Documentation.Text "")], emitter))
        let obj = TheGamma.Type.Object { Members = membs }
        obj, id
    | _ -> 
        Browser.console.log("getTypeAndEmitter: Cannot handle %O", ty)
        failwith "getTypeAndEmitter: Cannot handle type"

  [<Fable.Core.Emit("$0[$1]")>]
  let getProperty<'T> (obj:obj) (name:string) : 'T = failwith "never"

  let mapParamType = function
    | "int" | "float" -> PrimitiveType.Number
    | _ -> failwith "mapParamType: Unsupported parameter type"

  let restTypeCache = System.Collections.Generic.Dictionary<_, _>()

  let rec createRestType lookupNamed root cookies url = 
    let guid = (concatUrl root url) + cookies
    match restTypeCache.TryGetValue guid with
    | true, res -> res
    | _ ->
      let future = async {
        let! members = load (concatUrl root url) cookies 
        return 
          Type.Object
            { Members = 
                members |> Array.map (fun m ->
                  let schema = 
                    match m.schema with
                    | Some s -> [{ Type = getProperty s "@type"; Context = "http://schema.org"; Data = s }]
                    | _ -> []
                  match m.returns.kind with
                  | "nested" ->
                      let returns = unbox<TypeNested> m.returns 
                      let retTyp = createRestType lookupNamed root cookies returns.endpoint
                      match m.parameters with 
                      | Some parameters ->
                          let args = [ for p in parameters -> p.name, false, Type.Primitive (mapParamType p.``type``)] // TODO: Check this is OK type
                          let argNames = [ for p in parameters -> p.name ]
                          Member.Method(m.name, args, retTyp, [docMeta (parseDoc m.documentation)], methCall argNames m.trace)
                      | None -> 
                          Member.Property(m.name, retTyp, (docMeta (parseDoc m.documentation))::schema, propAccess m.trace) 
                  | "primitive" ->  
                      let returns = unbox<TypePrimitive> m.returns                      
                      let ty = fromRawType returns.``type``
                      let typ, parser = getTypeAndEmitter lookupNamed ty
                      Member.Property(m.name, typ, (docMeta (parseDoc m.documentation))::schema, dataCall parser m.trace returns.endpoint)
                  | _ -> failwith "?" ) } }
      let ty = Type.Delayed(guid, Async.CreateNamedFuture guid future)
      restTypeCache.[guid] <- ty
      ty

  let rec provideRestType lookupNamed name root cookies = 
    let ctx = ident("RuntimeContext")
    ProvidedType.GlobalValue
      ( name, [],
        NewExpression(ctx, [str root; str cookies; str ""], None),
        createRestType lookupNamed root cookies "/")

  // ------------------------------------------------------------------------------------------------
  //
  // ------------------------------------------------------------------------------------------------
  (*
  let nada = { Emit = fun (inst, args) -> Babel.NullLiteral(None) }

  let rec seriesTy() = 
    { new Future<_> with
        member x.Then(f) = 
          Type.Object 
            { Members = 
              [ Member.Method("sortValues", ["reverse", Type.Primitive "bool"], seriesTy (), nada)
                Member.Method("take", ["count", Type.Primitive "num"], seriesTy (), nada) ] } |> f } |> Type.Delayed

  let worldTy = 
    Type.Object
      { Members = 
          [ Member.Property("CO2 emissions (kt)", seriesTy (), nada) ] }

        *)