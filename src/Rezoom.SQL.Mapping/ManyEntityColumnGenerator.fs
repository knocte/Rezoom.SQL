﻿namespace Rezoom.SQL.Mapping.CodeGeneration
open Rezoom.SQL.Mapping
open LicenseToCIL
open LicenseToCIL.Stack
open LicenseToCIL.Ops
open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit

type ManyColumnGeneratorCode<'a> =
    // may be called in code generated by ManyColumnGenerator
    static member SetReverse(collection : 'a EntityReader ICollection, columnId : ColumnId, parent : obj) =
        for reader in collection do
            reader.SetReverse(columnId, parent)

[<CustomEquality>]
[<CustomComparison>]
type FastTuple<'a, 'b>(item1 : 'a, item2 : 'b) =
    struct
        static let equalityA = EqualityComparer<'a>.Default
        static let equalityB = EqualityComparer<'b>.Default
        static let comparerA = Comparer<'a>.Default
        static let comparerB = Comparer<'b>.Default
        member __.Item1 = item1
        member __.Item2 = item2
        member this.Equals(other : FastTuple<'a, 'b>) =
            equalityA.Equals(item1, other.Item1)
            && equalityB.Equals(item2, other.Item2)
        member this.CompareTo(other : FastTuple<'a, 'b>) =
            let a = comparerA.Compare(item1, other.Item1)
            if a <> 0 then a
            else comparerB.Compare(item2, other.Item2)
        override this.Equals(other : obj) =
            match other with
            | :? FastTuple<'a, 'b> as other -> this.Equals(other)
            | _ -> false
        override this.GetHashCode() =
            let h1 = equalityA.GetHashCode(item1)
            ((h1 <<< 5) + h1) ^^^ equalityB.GetHashCode(item2)
        interface IEquatable<FastTuple<'a, 'b>> with
            member this.Equals(other) = this.Equals(other)
        interface IComparable<FastTuple<'a, 'b>> with
            member this.CompareTo(other) = this.CompareTo(other)
    end

[<NoComparison>]
[<NoEquality>]
type private KeyColumns =
    {
        Type : Type
        ColumnInfoFields : TypeBuilder -> string -> obj
        ProcessColumns : Local -> obj -> Op<E S S, E S> // this, this -> this
        ImpartToNext : obj -> Op<E S S, E S> // this, that -> this
        Read : Local -> Label<E S> -> obj -> Op<E S, E S>
    }
    static member private GetPrimitiveConverter(column : Column) =
        match column.Blueprint.Value.Cardinality with
        | Many _ -> failwith "Collection types are not supported as keys"
        | One { Shape = Primitive prim } -> prim.Converter
        | One { Shape = Composite _ } ->
            failwith <|
                "Composite types are not supported as keys."
                + " Consider using KeyAttribute on multiple primitive columns instead."
    static member TupleTypeDef(length : int) =
        match length with
        | 2 -> typedefof<FastTuple<_, _>>
        | 3 -> typedefof<_ * _ * _>
        | 4 -> typedefof<_ * _ * _ * _>
        | 5 -> typedefof<_ * _ * _ * _ * _>
        | 6 -> typedefof<_ * _ * _ * _ * _ * _>
        | 7 -> typedefof<_ * _ * _ * _ * _ * _ * _>
        | 8 -> typedefof<_ * _ * _ * _ * _ * _ * _ * _>
        | 9 -> typedefof<_ * _ * _ * _ * _ * _ * _ * _ * _>
        | length -> failwithf "Unsupported length: can't use %d columns as identity" length
    static member Of(column : Column) =
        {   Type = column.Blueprint.Value.Output
            ColumnInfoFields = fun builder name ->
                builder.DefineField("_m_key_" + name, typeof<ColumnInfo>, FieldAttributes.Private) |> box
            ProcessColumns = fun subMap infoFields ->
                let infoField = infoFields |> Unchecked.unbox : FieldInfo
                cil {
                    yield ldloc subMap // this, col map
                    yield ldstr column.Name
                    yield call2 ColumnMap.ColumnMethod
                    yield stfld infoField
                }
            ImpartToNext = fun infoFields ->
                let infoField = infoFields |> Unchecked.unbox : FieldInfo
                cil {
                    yield ldarg 0
                    yield ldfld infoField
                    yield stfld infoField
                }
            Read = fun keyLocal skip infoFields ->
                let converter = KeyColumns.GetPrimitiveConverter(column)
                let infoField = infoFields |> Unchecked.unbox : FieldInfo
                cil {
                    yield ldarg 1 // row
                    yield ldarg 0 // row, this
                    yield ldfld infoField // row, colinfo
                    yield ldfld (typeof<ColumnInfo>.GetField("Index")) // row, index
                    yield callvirt2 (typeof<Row>.GetMethod("IsNull")) // isnull
                    yield brtrue skip
                    yield ldarg 1 // row
                    yield ldarg 0 // row, this
                    yield ldfld infoField // row, colinfo
                    yield generalize2 converter // id
                    yield stloc keyLocal
                }
        }
    static member Of(columns : Column IReadOnlyList) =
        if columns.Count < 1 then failwith "Collections of types without identity are not supported"
        if columns.Count = 1 then KeyColumns.Of(columns.[0]) else
        let types = [| for column in columns -> column.Output |]
        let tupleType = KeyColumns.TupleTypeDef(columns.Count).MakeGenericType(types)
        let ctor = tupleType.GetConstructor(types)
        {   Type = tupleType
            ColumnInfoFields = fun builder name ->
                [|  for column in columns ->
                        builder.DefineField
                            ("_m_key_" + name + "_" + column.Name, typeof<ColumnInfo>, FieldAttributes.Private)
                |] |> box
            ProcessColumns = fun subMap infoFields ->
                let infoFields = infoFields |> Unchecked.unbox : FieldInfo array
                cil {
                    for column, infoField in Seq.zip columns infoFields do
                        yield dup
                        yield ldloc subMap // this, col map
                        yield ldstr column.Name
                        yield call2 ColumnMap.ColumnMethod
                        yield stfld infoField
                    yield pop
                }
            ImpartToNext = fun infoFields ->
                let infoFields = infoFields |> Unchecked.unbox : FieldInfo array
                cil {
                    for infoField in infoFields do
                        yield dup
                        yield ldarg 0
                        yield ldfld infoField
                        yield stfld infoField
                    yield pop
                }
            Read = fun keyLocal skip infoFields ->
                let infoFields = infoFields |> Unchecked.unbox : FieldInfo array
                cil {
                    let locals = new ResizeArray<_>()
                    for column, infoField in Seq.zip columns infoFields do
                        let! local = deflocal column.Output
                        locals.Add(local)
                        let converter = KeyColumns.GetPrimitiveConverter(column)
                        yield ldarg 1 // row
                        yield ldarg 0 // row, this
                        yield ldfld infoField // row, colinfo
                        yield ldfld (typeof<ColumnInfo>.GetField("Index")) // row, index
                        yield callvirt2 (typeof<Row>.GetMethod("IsNull")) // isnull
                        yield brtrue skip
                        yield ldarg 1 // row
                        yield ldarg 0 // row, this
                        yield ldfld infoField // row, colinfo
                        yield generalize2 converter // id
                        yield stloc local
                    for local in locals do
                        yield ldloc local
                        yield pretend
                    yield newobj'x ctor
                    yield stloc keyLocal
                }
        }

type private ManyEntityColumnGenerator
    ( builder
    , column : Column option
    , element : ElementBlueprint
    , conversion : ConversionMethod
    ) =
    inherit EntityReaderColumnGenerator(builder)
    let composite =
        match element.Shape with
        | Composite c -> c
        | Primitive _ -> failwith "Collections of primitives are not supported"
    let keyColumns = KeyColumns.Of(composite.Identity)
    let elemTy = element.Output
    let staticTemplate = Generation.readerTemplateGeneric.MakeGenericType(elemTy)
    let entTemplate = typedefof<_ EntityReaderTemplate>.MakeGenericType(elemTy)
    let elemReaderTy = typedefof<_ EntityReader>.MakeGenericType(elemTy)
    let dictTy = typedefof<Dictionary<_, _>>.MakeGenericType(keyColumns.Type, elemReaderTy)
    let requiresSelf = composite.ReferencesQueryParent
    let mutable entDict = null
    let mutable refReader = null
    let mutable keyInfo = null
    override __.DefineConstructor() =
        let name = defaultArg (column |> Option.map (fun c -> c.Name)) "self"
        keyInfo <- keyColumns.ColumnInfoFields builder name
        entDict <- builder.DefineField("_m_d_" + name, dictTy, FieldAttributes.Private)
        refReader <- builder.DefineField("_m_r_" + name, elemReaderTy, FieldAttributes.Private)
        cil {
            yield ldarg 0
            yield newobj0 (dictTy.GetConstructor(Type.EmptyTypes))
            yield stfld entDict
        }
    override __.DefineProcessColumns() =
        cil {
            let! skip = deflabel
            yield ldarg 1 // col map
            match column with
            | Some column ->
                yield ldstr column.Name
                yield call2 ColumnMap.SubMapMethod
            | None -> ()
            let! sub = tmplocal typeof<ColumnMap>
            yield dup
            yield stloc sub // col map
            yield brfalse's skip
            yield dup // this
            yield keyColumns.ProcessColumns sub keyInfo
            yield cil {
                yield dup // this
                yield call0 (staticTemplate.GetMethod("Template")) // this, template
                yield callvirt1 (entTemplate.GetMethod("CreateReader")) // this, reader
                yield dup // this, reader, reader
                yield ldloc sub // this, reader, reader, submap
                yield callvirt2'void Generation.processColumnsMethod // this, reader
                yield stfld refReader // _
            }
            yield mark skip
        }
    override __.DefineImpartKnowledgeToNext() =
        cil {
            yield ldarg 1
            yield castclass builder
            yield keyColumns.ImpartToNext keyInfo

            let! nread = deflabel
            let! exit = deflabel
            yield dup
            yield ldfld refReader
            yield brfalse's nread
            yield cil {
                yield ldarg 1 // that
                yield ldarg 0 // that, this
                yield ldfld refReader // that, oldReader
                yield call0 (staticTemplate.GetMethod("Template")) // that, oldReader, template
                yield callvirt1 (entTemplate.GetMethod("CreateReader")) // that, oldReader, newReader
                let! newReader = deflocal elemReaderTy
                yield dup
                yield stloc newReader
                // that, oldReader, newReader
                yield callvirt2'void (elemReaderTy.GetMethod("ImpartKnowledgeToNext"))
                // that
                yield ldloc newReader
                yield stfld refReader
                yield br's exit
            }
            yield mark nread
            yield cil {
                yield ldarg 1
                yield ldnull
                yield stfld refReader
            }
            yield mark exit
        }
    override __.DefineRead(_) =
        cil {
            let! skip = deflabel
            yield dup
            yield ldfld refReader
            yield brfalse skip
            yield cil {
                let! keyLocal = tmplocal keyColumns.Type
                yield keyColumns.Read keyLocal skip keyInfo

                let! entReader = tmplocal elemReaderTy
                yield dup
                yield ldfld entDict
                yield ldloc keyLocal
                yield ldloca entReader
                yield call3 (dictTy.GetMethod("TryGetValue"))
                let! readRow = deflabel
                yield brtrue's readRow
                
                yield dup
                yield ldfld entDict
                yield ldloc keyLocal
                yield call0 (staticTemplate.GetMethod("Template"))
                yield callvirt1 (entTemplate.GetMethod("CreateReader"))
                yield dup
                yield stloc entReader
                yield call3'void (dictTy.GetMethod("Add", [| keyColumns.Type; elemReaderTy |]))
                yield dup
                yield ldfld refReader
                yield ldloc entReader
                yield callvirt2'void (elemReaderTy.GetMethod("ImpartKnowledgeToNext"))

                yield mark readRow
                yield ldloc entReader
                yield ldarg 1 // row
                yield callvirt2'void Generation.readMethod
            }
            yield mark skip
        }
    override __.RequiresSelfReferenceToPush = requiresSelf
    override __.DefinePush(self) =
        cil {
            let! ncase = deflabel
            yield ldarg 0
            yield ldfld entDict
            yield call1 (dictTy.GetProperty("Values").GetGetMethod())
            match column with
            | None -> ()
            | Some col ->
                match col.ReverseRelationship.Value with
                | None -> ()
                | Some rev ->
                    let setReverse =
                        typedefof<_ ManyColumnGeneratorCode>.MakeGenericType(elemTy).GetMethod("SetReverse")
                    yield dup
                    yield ldc'i4 rev.ColumnId
                    yield ldloc self
                    yield call3'void setReverse
            yield generalize conversion
        }
