module Bfi.Codegen

open System
open System.Reflection
open System.Reflection.Emit
open PtrHelper
open Bfi.Ast

type IL = ILGenerator

let inline ensureTypeOf() =
  let type' = Ptr<sbyte>.TypeOf
  if type'.Name <> "SByte*" then
    failwithf "Error: typeof(sbyte*) is %s, expected SByte*" type'.Name
  else
    type'

let sbytePtrType = ensureTypeOf()

[<Literal>]
let methodAttrs = MethodAttributes.Private ||| MethodAttributes.HideBySig ||| MethodAttributes.Static

[<Literal>]
let bindingFlags = BindingFlags.NonPublic ||| BindingFlags.Static

let readKey = typeof<Console>.GetMethod("ReadKey", Array.empty)
let getKeyChar = typeof<ConsoleKeyInfo>.GetProperty("KeyChar").GetMethod
let write = typeof<Console>.GetMethod("Write", [|typeof<char>|])

// AssemblyBuilderAccess.Save is (currently?) unavailable in .NET Core
// TODO: Find alternate library
let inline mkAssembly() = AssemblyBuilder.DefineDynamicAssembly(AssemblyName("BF"), AssemblyBuilderAccess.Run)

let inline mkModule (asm: AssemblyBuilder) = asm.DefineDynamicModule("Module")

let inline mkType (mdl: ModuleBuilder) = mdl.DefineType("Program")

let inline mkMethod (ty: TypeBuilder) = ty.DefineMethod("Main", methodAttrs, typeof<Void>, Array.empty)

let inline getIL (mtd: MethodBuilder) = mtd.GetILGenerator()

let inline emitTapeAlloc (il: ILGenerator) =
  il.DeclareLocal(sbytePtrType) |> ignore // typeof<_ nativeptr> always returns typeof<IntPtr> i F#, so I wrote a helper classlib in C#
  il.DeclareLocal(typeof<ConsoleKeyInfo>) |> ignore // Local needed for storing result of Console.ReadKey() to call .KeyChar on it

  il.Emit(OpCodes.Ldc_I4, 65536) // Pushes `65536` as i32 on stack
  il.Emit(OpCodes.Conv_U) // Converts to usize
  il.Emit(OpCodes.Localloc) // Pops usize and allocates that many bytes on the stack, pushing ptr on stack
  il.Emit(OpCodes.Stloc_0)

let inline emitRet (il: IL) =
  il.Emit(OpCodes.Ret)

let inline emitAdd (il: IL) (value: sbyte) =
  il.Emit(OpCodes.Ldloc_0)
  il.Emit(OpCodes.Dup)
  il.Emit(OpCodes.Ldind_I1)
  il.Emit(OpCodes.Ldc_I4_S, value)
  il.Emit(OpCodes.Add)
  il.Emit(OpCodes.Conv_I1)
  il.Emit(OpCodes.Stind_I1)

let inline emitMov (il: IL) (value: int) =
  il.Emit(OpCodes.Ldloc_0)
  il.Emit(OpCodes.Ldc_I4, value)
  il.Emit(OpCodes.Add)
  il.Emit(OpCodes.Stloc_0)

let inline emitSet (il: IL) (value: sbyte) =
  il.Emit(OpCodes.Ldloc_0)
  il.Emit(OpCodes.Ldc_I4_S, value)
  il.Emit(OpCodes.Stind_I1)

let inline emitRead (il: IL) =
  il.Emit(OpCodes.Ldloc_0)
  il.EmitCall(OpCodes.Call, readKey, Array.empty)
  il.Emit(OpCodes.Stloc_1)
  il.Emit(OpCodes.Ldloca_S, 1)
  il.EmitCall(OpCodes.Call, getKeyChar, Array.empty)
  il.Emit(OpCodes.Conv_I1)
  il.Emit(OpCodes.Stind_I1)

let inline emitWrite (il: IL) =
  il.Emit(OpCodes.Ldloc_0)
  il.Emit(OpCodes.Ldind_I1)
  il.Emit(OpCodes.Conv_U2)
  il.Emit(OpCodes.Call, write)

let rec emitLoop (il: IL) (ops: Op list) =
  let loopStart = il.DefineLabel()
  let loopEnd = il.DefineLabel()

  il.Emit(OpCodes.Br, loopEnd)

  il.MarkLabel(loopStart)

  emitOps' il ops
 
  il.MarkLabel(loopEnd)

  il.Emit(OpCodes.Ldloc_0)
  il.Emit(OpCodes.Ldind_I1)
  il.Emit(OpCodes.Brtrue, loopStart)

and emitOp (il: IL) (op: Op) =
  match op with
  | Add a -> emitAdd il a
  | Mov m -> emitMov il m
  | Set s -> emitSet il s
  | Read -> emitRead il
  | Write -> emitWrite il
  | Loop ops -> emitLoop il ops

and emitOps' il ops =
  match ops with
  | [] -> ()
  | op :: rest ->
      emitOp il op
      emitOps' il rest

let inline emitOps il ops =
  emitTapeAlloc il
  emitOps' il ops
  emitRet il

let inline compile ops =
  let ty =
    mkAssembly()
    |> mkModule
    |> mkType

  let il =
    ty
    |> mkMethod
    |> getIL

  emitOps il ops
  ty.CreateType().GetMethod("Main", bindingFlags)

let inline run (mtd: MethodInfo) =
  mtd.Invoke(null, Array.empty) |> ignore