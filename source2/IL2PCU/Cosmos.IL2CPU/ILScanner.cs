﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Indy.IL2CPU;
using Indy.IL2CPU.Plugs;
using Indy.IL2CPU.IL;

namespace Cosmos.IL2CPU {
  public class ILScanner : IDisposable {
    protected ILReader mReader;
    protected Assembler mAsmblr;

    // Contains known types and methods, both scanned and unscanned
    protected HashSet<object> mItems = new HashSet<object>();
    // Contains items to be scanned, both types and methods
    protected Queue<object> mQueue = new Queue<object>();
    // Virtual methods are nasty and constantly need to be rescanned for
    // overriding methods in new types, so we keep track of them separately. 
    // They are also in the main mItems and mQueue.
    protected HashSet<MethodBase> mVirtuals = new HashSet<MethodBase>();

    // Contains a list of plug implementor classes
    // Key = Target Class
    // Value = List of Implementors. There may be more than one
    protected Dictionary<Type, List<Type>> mPlugImpls = new Dictionary<Type, List<Type>>();
    // List of inheritable plugs. Plugs that start at an ancestor and plug all 
    // descendants. For example, delegates
    protected Dictionary<Type, List<Type>> mPlugImplsInhrt = new Dictionary<Type, List<Type>>();

    // Logging
    // Only use for debugging and profiling.
    protected bool mLogEnabled = false;
    protected string mMapPathname;
    protected TextWriter mLogWriter;
    protected struct LogItem {
      public string SrcType;
      public object Item;
    }
    protected Dictionary<object, List<LogItem>> mLogMap;

    //TODO: Look for Field plugs

    // System.ThrowHelper exists in MS .NET twice... 
    // Its an internal class that exists in both mscorlib and system assemblies.
    // They are separate types though, so normally the scanner scans both and
    // then we get conflicting labels. MS included it twice to make exception 
    // throwing code smaller. They are internal though, so we cannot
    // reference them directly and only via finding them as they come along.
    // We find it here, not via QueueType so we only check it here. Later
    // we might have to checkin QueueType also.
    // For now we accept both types, and just emit code for only one. This works
    // with the current Nasm assembler as we resolve by name in the assembler.
    // However with other assemblers this approach may not work.
    // If AssemblerNASM adds assembly name to the label, this will allow
    // both to exist as they do in BCL.
    // So in the future we might be able to remove this hack, or change
    // how it works.
    //  private Type mThrowHelper;

    public ILScanner(Assembler aAsmblr) {
      mAsmblr = aAsmblr;
      mReader = new ILReader();
      //mThrowHelper = typeof(object).Assembly.GetType("System.ThrowHelper");
    }

    public void EnableLogging(string aPathname) {
      mLogMap = new Dictionary<object, List<LogItem>>();
      mMapPathname = aPathname;
      mLogEnabled = true;
    }

    protected void Queue(object aItem, object aSrc, string aSrcType) {
      if (!mItems.Contains(aItem)) {
        if (mLogEnabled) {
          LogMapPoint(aSrc, aSrcType, aItem);
        }
        mItems.Add(aItem);
        mQueue.Enqueue(aItem);
      }
    }

    public void Execute(System.Reflection.MethodInfo aStartMethod) {
      // TODO: Investigate using MS CCI
      // Need to check license, as well as in profiler
      // http://cciast.codeplex.com/

      // Methodology
      //
      // Ok - we've done the scanner enough times to know it needs to be
      // documented super well so that future changes won't inadvertently
      // break undocumented and unseen requirements.
      //
      // We've tried many approaches including recursive and additive scanning.
      // They typically end up being inefficient, overly complex, or both.
      //
      // -We would like to scan all types/methods so we can plug them.
      // -But we can't scan them utnil we plug them, becuase we will scan things
      // that plugs would remove/change the paths of.
      // -Plugs may also call methods which are also plugged.
      // -We cannot resolve plugs ahead of time but must do on the fly during
      // scanning.
      // -TODO: Because we do on the fly resolution, we need to add explicit
      // checking of plug classes and err when public methods are found that
      // do not resolve. Maybe we can make a list and mark, or rescan. Can be done
      // later or as an optional auditing step.
      //
      // This why in the past we had repetitive scans.
      //
      // Now we focus on more passes, but simpler execution. In the end it should
      // be eaiser to optmize and yield overall better performance. Most of the 
      // passes should be low overhead versus an integrated system which often 
      // would need to reiterate over items multiple times. So we do more loops on
      // with less repetitive analysis, instead of fewer loops but more repetition.
      //
      // -Locate all plug classes
      // -Scan from entry point collecting all types and methods while checking
      // for and following plugs
      // -For each type
      //    -Include all ancestors
      //    -Include all static constructors
      // -For each virtual method
      //    -Scan overloads in descendants until IsFinal, IsSealed or end
      //    -Scan base in ancestors until top or IsAbstract
      // -Go to scan types again, until no new ones found.
      // -Because the virtual method scanning will add to the list as it goes, maintain
      //  2 lists.
      //    -Known Types and Methods
      //    -Types and Methods in Queue - to be scanned
      // -Finally, do compilation
      
      FindPlugImpls();

    //    // Pull in extra implementations, GC etc.
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)RuntimeEngineRefs.InitializeApplicationRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)RuntimeEngineRefs.FinalizeApplicationRef, false);
    //    ////xScanner.QueueMethod(typeof(CosmosAssembler).GetMethod("PrintException"), true);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)VTablesImplRefs.LoadTypeTableRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)VTablesImplRefs.SetMethodInfoRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)VTablesImplRefs.IsInstanceRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)VTablesImplRefs.SetTypeInfoRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)VTablesImplRefs.GetMethodAddressForTypeRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)GCImplementationRefs.IncRefCountRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)GCImplementationRefs.DecRefCountRef, false);
    //    QueueMethod(null, "Explicit Entry", (System.Reflection.MethodInfo)GCImplementationRefs.AllocNewObjectRef, false);
    //    // for now, to ease runtime exception throwing
    //    QueueMethod(null, "Explicit Entry", typeof(ExceptionHelper).GetMethod("ThrowNotImplemented", BindingFlags.Static | BindingFlags.Public), false);
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )RuntimeEngineRefs.InitializeApplicationRef );
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )RuntimeEngineRefs.FinalizeApplicationRef );
    //    ////xScanner.QueueMethod(typeof(CosmosAssembler).GetMethod("PrintException"));
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )VTablesImplRefs.LoadTypeTableRef );
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )VTablesImplRefs.SetMethodInfoRef );
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )VTablesImplRefs.IsInstanceRef );
    //    //xScanner.Execute( ( System.Reflection.MethodInfo )VTablesImplRefs.SetTypeInfoRef );

      // Start from entry point of this program
      Queue(aStartMethod, null, "Entry Point");

      ScanQueue();

      // Now everything is scanned, lets assemble
      foreach (var xItem in mItems) {
        if (xItem is MethodBase) {
          var xMethod = (MethodBase)xItem;
    //      if (xMethod.Type != MethodInfo.TypeEnum.NeedsPlug) {
    //        ScanMethod(xMethod);
    //      } else {
    //        // todo: make this nicer
    //        // methods will call the old name, while it's not emitted. that's why we emit a "forwarding label" here.
    //        mAsmblr.GenerateMethodForward(xMethod, xMethod.PlugMethod);
          }
        }
        // mAsmblr.GenerateVMTCode(mTypes, mTypesSet, mKnownMethods);
    }

    public void Dispose() {
      if (mLogEnabled) {
        // Create bookmarks, but also a dictionary that
        // we can find the items in
        var xBookmarks = new Dictionary<object, int>();
        int xBookmark = 0;
        foreach (var xList in mLogMap) {
          foreach (var xItem in xList.Value) {
            xBookmarks.Add(xItem.Item, xBookmark);
            xBookmark++;
          }
        }

        using (mLogWriter = new StreamWriter(mMapPathname, false)) {
          mLogWriter.WriteLine("<html><body>");
          foreach (var xList in mLogMap) {
            mLogWriter.WriteLine("<hr>");

            // Emit bookmarks above source, so when clicking links user doesn't need
            // to constantly scroll up.
            foreach (var xItem in xList.Value) {
              mLogWriter.WriteLine("<a name=\"Item" + xBookmarks[xItem.Item].ToString() + "\"></a>");
            }

            int xHref;
            if (!xBookmarks.TryGetValue(xList.Key, out xHref)) {
              xHref = -1;
            }
            mLogWriter.Write("<p>");
            if (xHref >= 0) {
              mLogWriter.WriteLine("<a href=\"#Item" + xHref.ToString() + "\">");
            }
            if (xList.Key == null) {
              mLogWriter.WriteLine("Unspecified Source");
            } else {
              mLogWriter.WriteLine(LogItemText(xList.Key));
            }
            if (xHref >= 0) {
              mLogWriter.Write("</a>");
            }
            mLogWriter.WriteLine("</a></p>");

            mLogWriter.WriteLine("<ul>");
            foreach (var xItem in xList.Value) {
              mLogWriter.Write("<li>" + LogItemText(xItem.Item) + "</li>");

              mLogWriter.WriteLine("<ul>");
              mLogWriter.WriteLine("<li>" + xItem.SrcType + "</<li>");
              mLogWriter.WriteLine("</ul>");
            }
            mLogWriter.WriteLine("</ul>");
          }
          mLogWriter.WriteLine("</body></html>");
        }
      }
    }

    public int MethodCount {
      get {
        return 0;
      }
    }

    protected string LogItemText(object aItem) {
      if (aItem is MethodBase) {
        var x = (MethodBase)aItem;
        return "Method: " + x.DeclaringType + "." + x.Name + "<br>" + x.GetFullName();
      } else if (aItem is Type) {
        var x = (Type)aItem;
        return "Type: " + x.FullName;
      } else {
        return "Other: " + aItem.ToString();
      }
    }

    protected void FindPlugImpls() {
      // TODO: New plug system, common plug base which all descend from
      // It can have a "this" member and then we
      // can separate static from instance by the static keyword
      // and ctors can be static "ctor" by name
      // Will still need plug attrib though to specify target
      // Also need to handle asm plugs, but those will be different anyways
      // TODO: Allow whole class plugs? ie, a class that completely replaces another class
      // and is substituted on the fly? Plug scanner would direct all access to that
      // class and throw an exception if any method, field, member etc is missing.
      foreach (var xAsm in AppDomain.CurrentDomain.GetAssemblies()) {
        if (xAsm.GetName().Name == "Indy.IL2CPU.X86") {
          //TODO: skip this assembly for now. Why?
          continue;
        }
        // Find all classes marked as a Plug
        foreach (var xPlugType in xAsm.GetTypes()) {
          // Foreach, it is possible there could be one plug class with mult plug targets
          foreach (PlugAttribute xAttrib in xPlugType.GetCustomAttributes(typeof(PlugAttribute), false)) {
            var xTargetType = xAttrib.Target;
            // If no type is specified, try to find by a specified name.
            // This is needed in cross assembly references where the
            // plug cannot reference the assembly of the target type
            if (xTargetType == null) {
              xTargetType = Type.GetType(xAttrib.TargetName, true, false);
            }
            // Only keep this plug if its for MS.NET.
            // TODO: Integrate with builder options to allow Mono support again.
            if (!xAttrib.IsMonoOnly) {
              var mPlugs = xAttrib.Inheritable ? mPlugImplsInhrt : mPlugImpls;
              List<Type> xImpls;
              if (mPlugs.TryGetValue(xTargetType, out xImpls)) {
                xImpls.Add(xPlugType);
              } else {
                xImpls = new List<Type>();
                xImpls.Add(xPlugType);
                mPlugs.Add(xTargetType, xImpls);
              }
            }
          }
        }
      }
    }

    protected void ScanMethod(MethodBase aMethod) {
      var xParams = aMethod.GetParameters();
      var xParamTypes = new Type[xParams.Length];
      for (int i = 0; i < xParams.Length; i++) {
        xParamTypes[i] = xParams[i].ParameterType;
      }

      // We only need to look in ancestors and descendants if the method is virtual
      if (aMethod.IsVirtual) {
        // If its virtual, and its not final or sealed then we
        // need to constanly watch for new descendant types which
        // might have overrides.
        if (aMethod.IsFinal || aMethod.DeclaringType.IsSealed) {
          mVirtuals.Add(aMethod);
          //TODO: We end up adding many overrides of each virtual
          //Would be great if we could not add descendant overrides
          //if bases are already in, or even better keep only the top 
          //base
        }

        //TODO: Do we need to queue the types of each param?
        //TODO: Do we need to queue DeclaringType

        // Look in list of types for ancestors and descendants
        // with overrides / bases
        //TODO: Cant be a foreach, collection gets modified
        // Second problem, hashsets cant be iterated by index
        // Back to 2 things - a hashset and list?
        foreach (var xItem in mItems) {
          if (xItem is Type) {
            var xType = (Type)xItem;
            // If DeclaringType is a ancestor or descendant
            if (xType.IsSubclassOf(aMethod.DeclaringType)
              || aMethod.DeclaringType.IsSubclassOf(xType)) {
              var xNewMethod = xType.GetMethod(aMethod.Name, xParamTypes);
              if (xNewMethod != null) {
                if (xNewMethod.IsAbstract) {
                  // If virtual, we need to add it to mVirtuals.
                  // Non virtuals will get added when they get scanned.
                  mVirtuals.Add(xNewMethod);
                } else {
                  // Abstract methods dont have an implementation, so only add
                  // non abstract methods for scanning.
                  Queue(xNewMethod, aMethod.DeclaringType, "Virtual");
                }
              }
            }
          }
        }
      }

      //TODO: Check to see if method is plugged, if it is we don't scan body
      var xPlug = ResolvePlug(aMethod, xParamTypes);
      if (xPlug == null) {
        //TODO: As we scan each method, we could update or put in a new list
        // that has the resolved plug so we don't have to reresolve it again
        // later for compilation.

        // Scan the method body for more type and method refs
        //TODO: Dont queue new items if they are plugged
        // or do we need to queue them with a resolved ref in a new list?
        var xOpCodes = mReader.ProcessMethod(aMethod);
        if (xOpCodes != null) {
          foreach (var xOpCode in xOpCodes) {
            if (xOpCode is ILOpCodes.OpMethod) {
              Queue(((ILOpCodes.OpMethod)xOpCode).Value, aMethod, "Call");
            } else if (xOpCode is ILOpCodes.OpType) {
              Queue(((ILOpCodes.OpType)xOpCode).Value, aMethod, "OpCode Value");
            } else if (xOpCode is ILOpCodes.OpField) {
              var xOpField = (ILOpCodes.OpField)xOpCode;
              //TODO: Need to do this? Will we get a ILOpCodes.OpType as well?
              Queue(xOpField.Value.DeclaringType, aMethod, "OpCode Value");
              if (xOpField.Value.IsStatic) {
                //TODO: Why do we add static fields, but not instance?
                // TODO: Add fields
              }
            }
          }
        }
      }
    }

    protected void ScanType(Type aType) {
      // Add immediate ancestor type
      // We dont need to crawl up farther, when the BaseType is scanned 
      // it will add its BaseType, and so on.
      if (aType.BaseType != null) {
        Queue(aType.BaseType, aType, "Base Type");
      }
      // Queue static ctors
      // We always need static ctors, else the type cannot 
      // be created.
      foreach (var xCctor in aType.GetConstructors(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) {
        if (xCctor.DeclaringType == aType) {
          Queue(xCctor, aType, "Static Constructor");
        }
      }

      //TODO: Scan mVirtuals for new possible overrides
      // in our new type if its a descendant of something in 
      // mVirtuals.
      //TODO: Is there a better way than rescanning every time like this?
    }

    protected void ScanQueue() {
      while (mQueue.Count > 0) {
        var xItem = mQueue.Dequeue();
        // Check for MethodBase first, they are more numerous 
        // and will reduce compares
        if (xItem is MethodBase) {
          ScanMethod((MethodBase)xItem);
        } else if (xItem is Type) {
          ScanType((Type)xItem);
        } else {
          throw new Exception("Unknown item found in queue.");
        }
      }
    }

    protected void LogMapPoint(object aSrc, string aSrcType, object aItem) {
      // Keys cant be null. If null, we just say ILScanner is the source
      if (aSrc == null) {
        aSrc = typeof(ILScanner);
      }

      var xLogItem = new LogItem() {
        SrcType = aSrcType,
        Item = aItem
      };
      List<LogItem> xList;
      if (!mLogMap.TryGetValue(aSrc, out xList)) {
        xList = new List<LogItem>();
        mLogMap.Add(aSrc, xList);
      }
      xList.Add(xLogItem);
    }

    protected MethodBase ResolvePlug(Type aTargetType, List<Type> aImpls
      , MethodBase aMethod, Type[] aParamTypes) 
    {
      //TODO: This method is "reversed" from old - remember that when porting
      MethodBase xResult = null;

      //TODO: Need to search for ctor plugs

      // Setup param types for search
      Type[] xParamTypes;
      if (aMethod.IsStatic) {
        xParamTypes = aParamTypes;
      } else {
        // If its an instance method, we have to add this to the ParamTypes to search
        xParamTypes = new Type[aParamTypes.Length + 1];
        if (aParamTypes.Length > 0) {
          aParamTypes.CopyTo(xParamTypes, 1);
        }
        xParamTypes[0] = aTargetType;
      }

      PlugMethodAttribute xAttrib = null;
      foreach (var xImpl in aImpls) {
        // Plugs methods must be static, and public
        // Search for non signature matches first since signature searches are slower
        xResult = xImpl.GetMethod(aMethod.Name, BindingFlags.Static | BindingFlags.Public
          , null, xParamTypes, null);
        if (xResult == null) {
          // Search by signature
          foreach (var xSigMethod in xImpl.GetMethods(BindingFlags.Static | BindingFlags.Public)) {
          }
        }
      }

      //TODO: For signature ones, we could cache the attrib. Thats 
      // why we check for null here
      if (xAttrib == null) {
        // TODO: Only allow one, but this code for now takes the last one
        // if there is more than one
        foreach (PlugMethodAttribute x in xResult.GetCustomAttributes(typeof(PlugMethodAttribute), false)) {
          xAttrib = x;
        }
      }

      // See if we need to disable this plug
      if (xAttrib != null) {
        if (!xAttrib.Enabled) {
          xResult = null;
        } else if (xAttrib.IsMonoOnly) {
          //TODO: Check this against build options
          //TODO: Two exclusive IsOnly's dont make sense
          // refactor these as a positive rather than negative
          // Same thing at type plug level
          xResult = null;
        }
      }
          //Type xAssembler = null;
            //  } else if (xAttrib.Signature != null) {
                // System_Void__Indy_IL2CPU_Assembler_Assembler__cctor__
                // If signature exists, the search is slow. Signatures
                // are infrequent though, so for now we just go slow method
                // and have not optimized or cached this info. When we
                // redo the plugs, we can fix this.
                //
                //              foreach (var xTargetMethod in xTargetMethods) {
                //                string sName = DataMember.FilterStringForIncorrectChars(MethodInfoLabelGenerator.GenerateFullName(xTargetMethod));
                //                if (string.Compare(sName, xMethodAttrib.Signature, true) == 0) {
                //                  uint xUID = QueueMethod(xPlugImpl.Plug, "Plug", xMethod, true);
                //                  mMethodPlugs.Add(xTargetMethod, new PlugInfo(xUID, xMethodAttrib.Assembler));
                //                  // Mark as disabled, because we already handled it
                //                  xEnabled = false;
                //                  break;
                //                }
                //              }
                //              // if still enabled, we didn't find our method
                //              if (xEnabled) {
                //                // todo: more precise error: imagine having a 100K line project, and this error happens...
                //                throw new Exception("Plug target method not found.");
                //              }
                //            } else {
                //              xEnabled = xMethodAttrib.Enabled;
                //            }
                //            xAssembler = xMethodAttrib.Assembler;
                //          }
              //}

              //if (xEnabled) {
                //          // for PlugMethodAttribute:
                //          //TODO: public string Signature;
                //          //[PlugMethod(Signature = "System_Void__Indy_IL2CPU_Assembler_Assembler__cctor__")]
                //          //TODO: public Type Assembler = null;
                //          // Scan the plug implementation
                //          uint xUID = QueueMethod(xPlugImpl.Plug, "Plug", xMethod, true);

                //          // Add the method to the list of plugged methods
                //          var xParams = xMethod.GetParameters();
                //          //TODO: Static method plugs dont seem to be separated 
                //          // from instance ones, so the only way seems to be to try
                //          // to match instance first, and if no match try static.
                //          // I really don't like this and feel we need to find
                //          // an explicit way to determine or mark the method 
                //          // implementations.
                //          //
                //          // Plug implementations take "this" as first argument
                //          // so when matching we don't include it in the search
                //          Type[] xTypesInst = null;
                //          var xActualParamCount = xParams.Length;
                //          foreach (var xParam in xParams) {
                //            if (xParam.GetCustomAttributes(typeof(FieldAccessAttribute), false).Length > 0) {
                //              xActualParamCount--;
                //            }
                //          }
                //          Type[] xTypesStatic = new Type[xActualParamCount];
                //          // If 0 params, has to be a static plug so we skip
                //          // any copying and leave xTypesInst = null
                //          // If 1 params, xTypesInst must be converted to Type[0]
                //          if (xActualParamCount == 1) {
                //            xTypesInst = new Type[0];
                //            xTypesStatic[0] = xParams[0].ParameterType;
                //          } else if (xActualParamCount > 1) {
                //            xTypesInst = new Type[xActualParamCount - 1];
                //            var xCurIdx = 0;
                //            foreach (var xParam in xParams.Skip(1)) {
                //              if (xParam.GetCustomAttributes(typeof(FieldAccessAttribute), false).Length > 0) {
                //                continue;
                //              }
                //              xTypesInst[xCurIdx] = xParam.ParameterType;
                //              xCurIdx++;
                //            }
                //            xCurIdx = 0;
                //            foreach (var xParam in xParams) {
                //              if (xParam.GetCustomAttributes(typeof(FieldAccessAttribute), false).Length > 0) {
                //                xCurIdx++;
                //                continue;
                //              }
                //              if (xCurIdx >= xTypesStatic.Length) {
                //                break;
                //              }
                //              xTypesStatic[xCurIdx] = xParam.ParameterType;
                //              xCurIdx++;
                //            }
                //          }
                //          System.Reflection.MethodBase xTargetMethod = null;
                //          // TODO: In future make rule that all ctor plugs are called
                //          // ctor by name, or use a new attrib
                //          //TODO: Document all the plug stuff in a document on website
                //          //TODO: To make inclusion of plugs easy, we can make a plugs master
                //          // that references the other default plugs so user exes only 
                //          // need to reference that one.
                //          // TODO: Skip FieldAccessAttribute if in impl
                //          if (xTypesInst != null) {
                //            if (string.Compare(xMethod.Name, "ctor", true) == 0) {
                //              xTargetMethod = xPlugImpl.Target.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, CallingConventions.Any, xTypesInst, null);
                //            } else {
                //              xTargetMethod = xPlugImpl.Target.GetMethod(xMethod.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, CallingConventions.Any, xTypesInst, null);
                //            }
                //          }
                //          // Not an instance method, try static
                //          if (xTargetMethod == null) {
                //            if (string.Compare(xMethod.Name, "cctor", true) == 0
                //              || string.Compare(xMethod.Name, "ctor", true) == 0) {
                //              xTargetMethod = xPlugImpl.Target.GetConstructor(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, CallingConventions.Any, xTypesStatic, null);
                //            } else {
                //              xTargetMethod = xPlugImpl.Target.GetMethod(xMethod.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, CallingConventions.Any, xTypesStatic, null);
                //            }
                //          }
                //          if (xTargetMethod == null) {
                //            throw new Exception("Plug target method not found.");
                //          }
                //          if (mMethodPlugs.ContainsKey(xTargetMethod)) {
                //            var xTheMethod = mMethodsToProcess[(int)mMethodPlugs[xTargetMethod].TargetUID];
                //            Console.Write("");
                //          }
                //          mMethodPlugs.Add(xTargetMethod, new PlugInfo(xUID, xAssembler));
                //        }
              //}
           // }
          //}
        //}
      
      return xResult;
    }

    protected MethodBase ResolvePlug(MethodBase aMethod, Type[] aParamTypes) {
      MethodBase xResult = null;

      // TODO: Right now plugs are copmiled in, even if they are not needed.
      // Maybe change this so plugs that are not needed are not compiled in?
      // To do so, maybe plugs could be marked as they are used

      List<Type> xImpls;
      // Check for exact type plugs first, they have precedence
      if (mPlugImpls.TryGetValue(aMethod.DeclaringType, out xImpls)) {
        xResult = ResolvePlug(aMethod.DeclaringType, xImpls, aMethod, aParamTypes);
      }

      // Check for inheritable plugs second.
      // We also need to fall through at method level, not just type.
      // That is a exact type plug could exist, but not method match.
      // In such a case the Inheritable methods should still be searched
      // if there is a inheritable type match.
      if (xResult == null) {
        foreach (var xInheritable in mPlugImplsInhrt) {
          if (aMethod.DeclaringType.IsSubclassOf(xInheritable.Key)) {
            xResult = ResolvePlug(xInheritable.Key, xInheritable.Value, aMethod, aParamTypes);
          }
        }
      }

      return xResult;
    }

    // === Old code and comments ======================================================
 
  //  //TODO: These store the MethodBase which also have the IL for the body in memory
  //  // For large asms this could eat lot of RAM. Should convert this to remove
  //  // items from the list after they are processed but keep them in HashSet so we
  //  // know they are already done. Currently HashSet uses a reference though, so we
  //  // need to hash on some UID instead of the refernce. Do not use strings, they are
  //  // super slow.
  //  //	TODO: We need to scan for static fields too. 

  //  private void ScanMethod(MethodInfo aMethodInfo) {
  //    var xMethodBase = aMethodInfo.MethodBase;


  //      // Assemble the method
    //TODO: We have to load the opcodes twice, this might be slow,
    // but loading in RAM could consume lots of RAM
  //      if (aMethodInfo.MethodBase.DeclaringType != mThrowHelper) {
  //        mAsmblr.ProcessMethod(aMethodInfo, xOpCodes);
  //      }
  //    }
  //  }

  //  private void QueueField(object aSrc, string aSrcType, Cosmos.IL2CPU.ILOpCodes.OpField xOpField) {
  //    // todo: add log map thing?
  //    if (!mStaticFields.Contains(xOpField.Value)) {
  //      mStaticFields.Add(xOpField.Value);
  //      mAsmblr.ProcessField(xOpField.Value);

  //      QueueType(xOpField.Value, "FieldType", xOpField.Value.FieldType);
  //      QueueType(xOpField.Value, "DeclaringType", xOpField.Value.FieldType);
  //    }
  //  }

  //  // QueueMethod should only queue the method, and do no processing of the
  //  // body or resolution of its contents. It is called during plug resolution
  //  // etc and all further resolution should wait until all plugs are loaded.
  //  public uint QueueMethod(object aSrc, string aSrcType, MethodBase aMethodBase
  //    , bool aIsPlug)
  //  {
  //    uint xResult;

  //    xResult = (uint)mMethodsToProcess.Count;
  //    mKnownMethods.Add(aMethodBase, xResult);
  //    MethodInfo xPlug = null;
  //    Type xPlugAssembler = null;
  //    var xMethodType = MethodInfo.TypeEnum.Normal;
  //    if (aIsPlug) {
  //      xMethodType = MethodInfo.TypeEnum.Plug;
  //    } else {
  //      xMethodType = MethodInfo.TypeEnum.Normal;
  //      if ((aMethodBase.Attributes & MethodAttributes.PinvokeImpl) != 0) {
  //        // pinvoke methods dont have an embedded implementation
  //        xMethodType = MethodInfo.TypeEnum.NeedsPlug;
  //      } else {
  //        var xImplFlags = aMethodBase.GetMethodImplementationFlags();
  //        // todo: prob even more
  //        if ((xImplFlags & MethodImplAttributes.Native) != 0 ||
  //            (xImplFlags & MethodImplAttributes.InternalCall) != 0) {
  //          // native implementations cannot be compiled
  //          xMethodType = MethodInfo.TypeEnum.NeedsPlug;
  //        }
  //      }

  //      // See if method has a plug
  //      PlugInfo xPlugInfo = null;
  //      uint xPlugId = 0;
  //      if (mMethodPlugs.TryGetValue(aMethodBase, out xPlugInfo)) {
  //        xPlugId = xPlugInfo.TargetUID;
  //        xPlug = mMethodsToProcess[(int)xPlugId];
  //        xPlugAssembler = xPlugInfo.PlugMethodAssembler;
  //      } else {
  //        // See if we need to apply a inheritable plug
  //        // Do this after explicit plugs to allow precedence
  //        foreach (var xInheritablePlug in mPlugInheritableImpls) {
  //          if (aMethodBase.DeclaringType.IsSubclassOf(xInheritablePlug.Target)) {

  //          }
  //        }
  //      }

  //      if (xMethodType == MethodInfo.TypeEnum.NeedsPlug && xPlug == null && xPlugAssembler == null) {
  //        throw new Exception("Method [" + aMethodBase.DeclaringType + "." + aMethodBase.Name + "] needs to be plugged, but wasn't");
  //      }
  //    }

  //    var xMethod = new MethodInfo(aMethodBase, xResult, xMethodType, xPlug, xPlugAssembler);
  //    mMethodsToProcess.Add(xMethod);

  //    if(!aIsPlug) {
  //      // Queue Types directly related to method
  //      QueueType(aMethodBase, "Declaring Type", aMethodBase.DeclaringType);
  //      if (aMethodBase is System.Reflection.MethodInfo) {
  //        QueueType(aMethodBase, "Return Type", ((System.Reflection.MethodInfo)aMethodBase).ReturnType);
  //      }
  //      foreach (var xParam in aMethodBase.GetParameters()) {
  //        QueueType(aMethodBase, "Parameter", xParam.ParameterType);
  //      }
  //    }
  //    return xResult;
  //  }

  //  //protected void QueueStaticField(FieldInfo aFieldInfo) {
  //  //  if (!mFieldsSet.Contains(aFieldInfo)) {
  //  //    if (!aFieldInfo.IsStatic) {
  //  //      throw new Exception("Cannot queue instance fields!");
  //  //    }
  //  //    mFieldsSet.Add(aFieldInfo);
  //  //    QueueType(aFieldInfo.DeclaringType);
  //  //    QueueType(aFieldInfo.FieldType);
  //  //  }
  //  //}

  }
}
