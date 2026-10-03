using System;using System.Collections.Generic;using System.Linq;using System.Reflection;using System.Reflection.Emit;using System.Text;
class P{
 static string ManagedDir="";
 static string ExtraDir="";
 static readonly Dictionary<short,OpCode> Ops=new();
 static P(){
  AppDomain.CurrentDomain.AssemblyResolve += (s,e)=>{ try{ var nm=new AssemblyName(e.Name).Name+".dll"; var fp=System.IO.Path.Combine(ManagedDir,nm); if(System.IO.File.Exists(fp)) return Assembly.LoadFrom(fp);
    if(ExtraDir.Length>0){ var fp2=System.IO.Path.Combine(ExtraDir,nm); if(System.IO.File.Exists(fp2)) return Assembly.LoadFrom(fp2); } }catch{} return null; };
  foreach(var f in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)){var o=(OpCode)f.GetValue(null);Ops[o.Value]=o;}
 }
 static string IL(MethodBase m){
  try{var b=m.GetMethodBody();if(b==null)return "(no body)";
   var il=b.GetILAsByteArray();var sb=new StringBuilder();int i=0;var mod=m.Module;
   while(i<il.Length){int off=i;short v=il[i++];if(v==0xFE)v=(short)(0xFE00|il[i++]);
    if(!Ops.TryGetValue(v,out var op)){sb.Append($"IL_{off:X4}: <0x{v:X}> ");continue;}
    sb.Append($"IL_{off:X4}: {op.Name}");
    switch(op.OperandType){
     case OperandType.InlineNone:break;
     case OperandType.ShortInlineI:sb.Append(" "+(sbyte)il[i]);i++;break;
     case OperandType.InlineI:sb.Append(" "+BitConverter.ToInt32(il,i));i+=4;break;
     case OperandType.InlineI8:sb.Append(" "+BitConverter.ToInt64(il,i));i+=8;break;
     case OperandType.ShortInlineR:sb.Append(" "+BitConverter.ToSingle(il,i));i+=4;break;
     case OperandType.InlineR:sb.Append(" "+BitConverter.ToDouble(il,i));i+=8;break;
     case OperandType.ShortInlineBrTarget:sb.Append($" IL_{i+1+(sbyte)il[i]:X4}");i++;break;
     case OperandType.InlineBrTarget:sb.Append($" IL_{i+4+BitConverter.ToInt32(il,i):X4}");i+=4;break;
     case OperandType.ShortInlineVar:sb.Append(" V"+il[i]);i++;break;
     case OperandType.InlineVar:sb.Append(" V"+BitConverter.ToInt16(il,i));i+=2;break;
     case OperandType.InlineString:{int tk=BitConverter.ToInt32(il,i);i+=4;try{sb.Append(" \""+mod.ResolveString(tk)+"\"");}catch{sb.Append(" str#"+tk);}break;}
     case OperandType.InlineField:{int tk=BitConverter.ToInt32(il,i);i+=4;try{var f=mod.ResolveField(tk);sb.Append(" "+f.DeclaringType?.Name+"."+f.Name);}catch{sb.Append(" fld#"+tk);}break;}
     case OperandType.InlineMethod:{int tk=BitConverter.ToInt32(il,i);i+=4;try{var mm=mod.ResolveMethod(tk);sb.Append(" "+mm.DeclaringType?.Name+"."+mm.Name);}catch{sb.Append(" mth#"+tk);}break;}
     case OperandType.InlineType:{int tk=BitConverter.ToInt32(il,i);i+=4;try{sb.Append(" "+mod.ResolveType(tk).Name);}catch{sb.Append(" typ#"+tk);}break;}
     case OperandType.InlineTok:{int tk=BitConverter.ToInt32(il,i);i+=4;sb.Append(" tok#"+tk);break;}
     case OperandType.InlineSig:{i+=4;break;}
     case OperandType.InlineSwitch:{int n=BitConverter.ToInt32(il,i);i+=4+4*n;sb.Append(" switch["+n+"]");break;}
     default:break;}
    sb.AppendLine();}
   return sb.ToString();}catch(Exception e){return "(IL 失败: "+e.GetType().Name+" "+e.Message+")";}}
 static void Dump(Type t){
  Console.WriteLine($"===== [IL] {t.FullName} (base={t.BaseType?.Name}) =====");
  var isEnum=t.IsEnum;
  foreach(var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)){
   var cv=""; if(isEnum){try{cv=" = "+Convert.ToInt64(f.GetRawConstantValue());}catch{}}
   Console.WriteLine($"  字段 {f.FieldType.Name} {f.Name}{cv}");}
  foreach(var p in t.GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static))
   Console.WriteLine($"  属性 {p.PropertyType.Name} {p.Name} get={p.GetMethod!=null} set={p.SetMethod!=null}");
  foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)){
   if(m.IsAbstract)continue;
   Console.WriteLine($"  方法 {m.ReturnType.Name} {m.Name}({string.Join(",",m.GetParameters().Select(x=>x.ParameterType.Name))})");
   Console.WriteLine(IL(m));}}
 static void Main(string[] a){
  if(a.Length<2){Console.WriteLine("用法: ildump <dll> [@type:名字 | 关键字]");return;}
  ManagedDir=System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(a[0]));
  ExtraDir = a.Length>2 ? a[2] : @"F:\SteamLibrary\steamapps\common\Card Survival Tropical Island\Card Survival - Tropical Island_Data\Managed";
  var asm=Assembly.LoadFrom(a[0]);
  Type[] all;try{all=asm.GetTypes();}catch(ReflectionTypeLoadException e){all=e.Types.Where(x=>x!=null).ToArray();}
  if(a[1].StartsWith("@type:")){
   var tn=a[1].Substring(6);
   var hits=all.Where(x=>x.Name==tn||x.FullName==tn).ToArray();
   Console.WriteLine("===== @type:"+tn+" 命中 "+hits.Length+" 个类型 =====");
   foreach(var h in hits) Dump(h);
   return;}
  var needle=a[1];
  Console.WriteLine("===== 引用 "+needle+" 的方法（全程序集，附完整 IL）=====");
  foreach(var t in all){
   MethodBase[] ms;
   try{ms=t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly);}catch{continue;}
   foreach(var m in ms){var s=IL(m);if(s.Contains(needle)){
    Console.WriteLine($"----- {t.FullName}.{m.Name}({string.Join(",",m.GetParameters().Select(x=>x.ParameterType.Name))}) -----");
    Console.WriteLine(s);}}}
 }
}