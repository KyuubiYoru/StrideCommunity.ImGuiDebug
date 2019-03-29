namespace XenkoCommunity.ImGuiDebug
{
    using Xenko.Core;
    
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Runtime.CompilerServices;
    using System.Numerics;
    
    using ImGuiDir = ImGuiNET.ImGuiDir;
    using static ImGuiNET.ImGui;
    using static ImGuiExtension;
    
    public class Inspector : BaseWindow
    {
        /// <summary> Array of all possible <see cref="Filter"/> values </summary>
        static readonly Filter[] FILTER_VALUES = (Filter[]) Enum.GetValues( typeof(Filter) );
        const float DUMMY_WIDTH = 19;
        const float INDENTATION2 = DUMMY_WIDTH+8;
        
        /// <summary> A UI handler function to draw and modify values </summary>
        public delegate bool ValueHandler( string label, ref object value );
        /// <summary> Add your drawing functions to explicitly override drawing for objects of the given type </summary>
        public static ConcurrentDictionary<Type, ValueHandler> ValueDrawingHandlers = new ConcurrentDictionary<Type, ValueHandler>();
        
        /// <summary> Any live inspectors </summary>
        static List<Inspector> _inspectors = new List<Inspector>();
        
        
        Dictionary<Type, TypeCache> _cachedTypeData = new Dictionary<Type, TypeCache>();
        /// <summary> Opened sub object of the inspected object in the tree view </summary>
        HashSet<int> _openedId = new HashSet<int>();
        /// <summary> Lets not keep references from being GCed </summary>
        WeakReference<object> _target = new WeakReference<object>(null);
        
        
        // Settings
        /// <summary> Is this interface returned by <see cref="FindFreeInspector"/> </summary>
        public bool Locked = false;
        /// <summary> Show specialized interface to handle IEnumerable types </summary>
        public bool EnumerableView = true;
        /// <summary> Members shown within the interface </summary>
        public Filter MemberFilter = Filter.Public | Filter.Inherited | Filter.Properties | Filter.Fields;
        /// <summary> The object to inspect </summary>
        public object Target
        {
            get
            {
                if( _target.TryGetTarget( out object target ) )
                    return target;
                return null;
            }
            set
            {
                if( _target == value )
                    return;
                
                _target.SetTarget( value );
                _openedId.Clear();
            }
        }
        
        
        // Cache to handle dictionary add() commands
        WeakReference<object> _dicAddCommandTarget = new WeakReference<object>(null);
        (object key, object value) _dicAddCommandData;
        
        public Inspector( IServiceRegistry services ) : base( services )
        {
            _inspectors.Add( this );
        }
        
        public static Inspector FindFreeInspector( IServiceRegistry services )
        {
            foreach( Inspector inspector in _inspectors )
            {
                if( ! inspector.Locked )
                    return inspector;
            }

            return new Inspector( services );
        }
        
        protected override void OnDestroy()
        {
            _inspectors.Remove( this );
        }




        protected override void OnDraw( bool collapsed )
        {
            if( collapsed )
                return;

            Checkbox( "Locked", ref Locked );
            Checkbox( "Enumerable view", ref EnumerableView );
            if( CollapsingHeader( "Filter" ) )
            {
                using( UColumns( 2 ) )
                {
                    uint filterImGui = (uint)MemberFilter;
                    foreach( Filter def in FILTER_VALUES )
                    {
                        CheckboxFlags( def.ToString(), ref filterImGui, (uint)def );
                        NextColumn();
                    }
                    // On change, clear members to force re-filter
                    if( filterImGui != (uint) MemberFilter )
                    {
                        MemberFilter = (Filter) filterImGui;
                        _cachedTypeData.Clear();
                    }
                }
            }
            
            Spacing();
            
            TextUnformatted( $"Inspecting [{Target ?? "null"}]" );
            Separator();
            
            using( Child() )
            {
                if( Target != null )
                    DrawMembers( Target, Target.GetType().GetHashCode() );
            }
        }

        bool DrawMembers( object target, int hashcodeSource )
        {
            if(target == null)
                return false;

            MemberInfo[] members = GetTypeData( target.GetType() ).FilteredMembers;

            bool hasChanged = false;
            using( UIndent( INDENTATION2 ) )
            {
                foreach( var member in members )
                {
                    object value;
                    bool readOnly;
                    { // Get value
                        try
                        {
                            if( member is FieldInfo fi )
                            {
                                value = fi.GetValue( target );
                                readOnly = false;
                            }
                            else if( member is PropertyInfo pi && pi.CanRead )
                            {
                                value = pi.GetValue( target );
                                readOnly = ! pi.CanWrite;
                            }
                            else 
                                continue;
                        }
                        catch( Exception e )
                        {
                            value = $"x Exception: {e.Message}";
                            readOnly = true;
                        }
                    }

                    bool changed = DrawValue(member.Name, ref value, readOnly, hashcodeSource);
                    if( changed && !readOnly )
                    {
                        hasChanged = true;
                        try
                        {
                            if( member is FieldInfo fi )
                                fi.SetValue( target, value );
                            else if( member is PropertyInfo pi )
                                pi?.SetValue(target, value);
                            else
                                throw new NotImplementedException();
                        }
                        catch( Exception e )
                        {
                            System.Console.Out?.WriteLine( e );
                        }
                    }
                }
                if( EnumerableView && target is IEnumerable ienum )
                    DrawIEnumTypes( target, ienum, hashcodeSource );
            }
            
            // structs have to bubble up their changes since the object
            // we get is not pointing to the source but is a copy of it instead
            return hasChanged && target.GetType().IsValueType;
        }
        
        bool DrawValue(string name, ref object value, bool readOnly, int hashcodeSource)
        {
            // Deterministic way to provide a hashcode in a hierarchic/recursive manner
            // The hashcode created here, properly create one specific code for this object at this place in the hierarchy
            // of course hashcodes still aren't unique but this should work well enough for now
            int memberInHierarchyId = (hashcodeSource, name).GetHashCode();
            PushID( memberInHierarchyId );
            
            if( value == null )
            {
                Dummy( new Vector2( DUMMY_WIDTH, 1 ) );
                using( UColumns( 2 ) )
                {
                    TextUnformatted( name );
                    NextColumn();
                    TextUnformatted( "null" );
                }
                return false;
            }

            TypeCache typeData = GetTypeData( value.GetType() );
            bool valueChanged = false;
            if( ValueDrawingHandlers.TryGetValue( value.GetType(), out var handler ) )
            {
                valueChanged = handler( name, ref value );
                return valueChanged;
            }
            
            bool recursable = Type.GetTypeCode( value.GetType() ) == TypeCode.Object;
            recursable = recursable && ( typeData.FilteredMembers.Length > 0 || ReadableIEnumerable(value) );
            
            bool recurse = recursable && _openedId.Contains( memberInHierarchyId );
            
            using( UColumns( 2 ) )
            {
                // Present button to recurse through value
                if( recursable )
                {
                    if( ArrowButton( "", recurse ? ImGuiDir.Down : ImGuiDir.Right ) )
                    {
                        if( recurse )
                            _openedId.Remove( memberInHierarchyId );
                        else
                            _openedId.Add( memberInHierarchyId );
                    }
                }
                else
                    Dummy( new Vector2( DUMMY_WIDTH, 1 ) );
                SameLine();
                TextUnformatted( name );
                
                NextColumn();
                
                // Complex object: present button to swap inspect target to this object ?
                if( Type.GetTypeCode( value.GetType() ) == TypeCode.Object && value.GetType().IsClass )
                {
                    if( Button( value.ToString() ) )
                        Target = value;
                    goto RECURSE;
                }
                // Basic value type: Present UI handler for values
                else if( readOnly == false )
                {
                    switch( value )
                    {
                        // if(valueChanged) => to cast / generate garbage only when the value changed
                        case bool v: valueChanged = Checkbox( "", ref v ); if(valueChanged){ value = v; } return valueChanged;
                        case string v: valueChanged = InputText( "", ref v, 99 ); if(valueChanged){ value = v; } return valueChanged;
                        case float v: valueChanged = InputFloat( "", ref v ); if(valueChanged){ value = v; } return valueChanged;
                        case double v: valueChanged = InputDouble( "", ref v ); if(valueChanged){ value = v; } return valueChanged;
                        case int v: valueChanged = InputInt( "", ref v ); if(valueChanged){ value = v; } return valueChanged;
                        // c = closest type that ImGui implements natively, manually cast it to the right type afterward
                        case uint v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (uint)c; } return valueChanged; }
                        case long v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (long)c; } return valueChanged; }
                        case ulong v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (ulong)c; } return valueChanged; }
                        case short v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (short)c; } return valueChanged; }
                        case ushort v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (ushort)c; } return valueChanged; }
                        case byte v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (byte)c; } return valueChanged; }
                        case sbyte v: { int c = (int)v; valueChanged = InputInt( "", ref c ); if(valueChanged){ value = (sbyte)c; } return valueChanged; }
                    }
                }
                if( typeData.asEnum != null )
                {
                    ( bool flags, Array values ) = typeData.asEnum.Value;
                    using( UCombo( "", value.ToString(), out bool open ) )
                    {
                        if( open )
                        {
                            foreach( object o in values )
                            {
                                ulong fieldValue = GetEnumBits( value );
                                ulong compValue = GetEnumBits( o );
                                bool selected;
                                if( flags )
                                    selected = ( fieldValue & compValue ) == compValue;
                                else
                                    selected = fieldValue == compValue;
                                
                                if( Selectable( o.ToString(), selected ) )
                                {
                                    if( flags )
                                    {
                                        if( selected ) // unselect this value
                                            compValue = fieldValue & ~compValue;
                                        else // select new value
                                            compValue = fieldValue | compValue;
                                    }
                                    value = GetEnumValueFromBits( compValue, value.GetType() );
                                    valueChanged = true;
                                }
                            }
                        }
                        return valueChanged;
                    }
                }
                
                // Otherwise, present basic read-only text
                TextUnformatted( value.ToString() );
            }
            
            RECURSE:
            
            if( recurse ) // Pass in this member's id to properly offset sub-members' hash
                valueChanged = valueChanged || DrawMembers( value, memberInHierarchyId );
            
            return valueChanged;
        }

        void DrawIEnumTypes( object target, IEnumerable ienum, int hashcodeSource )
        {
            using( UIndent() )
            {
                if( TryDrawAsIList( target, hashcodeSource ) )
                    return;
                
                if( TryDrawAsIDictionary( target, hashcodeSource ) )
                    return;
                
                Spacing();
                TextDisabled( "As Enumerable" );
                foreach( object o in ienum )
                {
                    object o2 = o;
                    using( UIndent() )
                        DrawValue( $"- {o2}", ref o2, true, hashcodeSource );
                }
            }
            Spacing();
        }

        bool TryDrawAsIDictionary( object target, int hashcodeSource )
        {
            var typeData = GetTypeData( target.GetType() );
            if( typeData.AsDictionary == null )
                return false;
            var data = typeData.AsDictionary.Value;
            Spacing();
            TextDisabled( "As Dictionary" );
            // Most of the management here is done through reflection
            // as the type might not implement IDictionary but just IDictionary<T> ...
            using( UIndent() )
            {
                { // Show dictionary content
                    // IDictionary.Keys
                    var keys = (data.getKey?.Invoke(target, null) as IEnumerable)?.GetEnumerator();
                    // IDictionary.Values
                    var values = (data.getValue?.Invoke(target, null) as IEnumerable)?.GetEnumerator();
                    if( keys == null || values == null )
                        return false;

                    bool removeKey = false;
                    object keyToRemove = null;
                    
                    bool changeKey = false;
                    object keyToChange = null;
                    object valueOfKeyToChange = null;
                    
                    while( keys.MoveNext() && values.MoveNext() )
                    {
                        var key = keys.Current;
                        var value = values.Current;
                        string keyString = key?.ToString() ?? "null";
                        bool changed = DrawValue( keyString, ref value, false, hashcodeSource );
                        if( changed )
                        {
                            changeKey = true;
                            keyToChange = key;
                            valueOfKeyToChange = value;
                        }
                        
                        SameLine();
                        PushID( keyString );
                        if( Button( "x" ) )
                        {
                            removeKey = true;
                            keyToRemove = key;
                        }
                    }

                    if( removeKey )
                    {
                        target.GetType().GetMethod( nameof(IDictionary.Remove), new [] { data.key } ) 
                            ? .Invoke( target, new[] { keyToRemove } );
                    }

                    if( changeKey )
                    {
                        // IDictionary[ keyToChange ] = valueOfKeyToChange
                        var parameters = new[] { keyToChange, valueOfKeyToChange };
                        target.GetType().GetProperty( "Item", data.value, new[] { data.key } ) ? 
                            .SetMethod.Invoke( target, parameters );
                    }
                }
                
                // Show upcoming key and value
                if( _dicAddCommandTarget.TryGetTarget( out var addActionTarget ) && addActionTarget == target )
                {
                    ( object key, object value ) = _dicAddCommandData;
                    DrawValue( "Upcoming Key:", ref key, false, hashcodeSource );
                    DrawValue( "Upcoming Value:", ref value, false, hashcodeSource );
                    _dicAddCommandData = ( key, value );
                    if( Button( "Cancel" ) )
                    {
                        _dicAddCommandData = ( null, null );
                        _dicAddCommandTarget.SetTarget( null );
                    }
                    SameLine();
                    if( Button( "Add" ) )
                    {
                        var parameters = new[] { key, value };
                        target.GetType().GetMethod( nameof(IDictionary.Add), new[] { data.key, data.value } )
                            ?.Invoke( target, parameters );
                        
                        _dicAddCommandData = ( null, null );
                        _dicAddCommandTarget.SetTarget( null );
                    }
                }
                // Prepare an add to the dictionary: create an editable instance for key and value
                else if( Button( "+", new Vector2( GetContentRegionAvailWidth(), GetTextLineHeightWithSpacing() ) ) )
                {
                    _dicAddCommandData = ( GetTypeData(data.key).NewObject(), GetTypeData(data.value).NewObject() );
                    _dicAddCommandTarget.SetTarget( target );
                }
            }

            return true;
        }

        bool TryDrawAsIList( object target, int hashcodeSource )
        {
            var typeData = GetTypeData( target.GetType() );
            if( typeData.AsList == null )
                return false;
            Spacing();
            TextDisabled( "As List" );
            // Most of the management here is done through reflection
            // as the type might not implement IList but just IList<T> ...
            using( UIndent() )
            {
                int i = 0;
                int? indexToRemove = null;
                int? indexToChange = null;
                object objectToAssign = null;
                foreach( object o in target as IEnumerable )
                {
                    object o2 = o;
                    bool changed = DrawValue( $"{i.ToString()}: {o}", ref o2, false, hashcodeSource );
                    if( changed )
                    {
                        indexToChange = i;
                        objectToAssign = o2;
                    }
                    
                    SameLine();
                    PushID(o.ToString());
                    if( Button( "x" ) )
                        indexToRemove = i;
                    
                    i++;
                }

                // Calling 'this[int indexToChange] = objectToAssign'
                if( indexToChange != null )
                {
                    var parameters = new[] { indexToChange.Value, objectToAssign };
                    target.GetType().GetProperty( "Item", typeData.AsList, new[] { typeof(int) } ) ? 
                        .SetMethod.Invoke( target, parameters );
                }
                // Calling 'RemoveAt(int index)'
                if( indexToRemove != null )
                {
                    target.GetType().GetMethod( nameof(IList.RemoveAt), new []{ typeof(int) } ) ? .Invoke( target, new object[] { indexToRemove.Value } );
                }

                // Calling 'Add(ObjectType object)'
                if( Button( "+", new Vector2( GetContentRegionAvailWidth(), GetTextLineHeightWithSpacing() ) ) )
                {
                    var valueType = typeData.AsList;
                    var value = GetTypeData(typeData.AsList).NewObject();
                    target.GetType().GetMethod( nameof(IList.Add), new []{ valueType } ) ? .Invoke( target, new [] { value } );
                }
            }

            return true;
        }
        
        TypeCache GetTypeData( Type t )
        {
            TypeCache output;
            if( _cachedTypeData.TryGetValue( t, out output ) )
                return output;
            
            output = new TypeCache( t, MemberFilter );
            _cachedTypeData.Add( t, output );
            return output;
        }

        static bool ReadableIEnumerable( object source )
        {
            if( source is IEnumerable ienum )
            {
                foreach( object o in ienum )
                    return true;
            }

            return false;
        }

        static ulong GetEnumBits( object enumValue )
        {
            var valueType = enumValue.GetType();
            if( valueType.IsEnum )
                valueType = Enum.GetUnderlyingType( valueType );
            
            if( valueType == typeof(int) ) return (ulong)(int)enumValue;
            if( valueType == typeof(uint) ) return (ulong)(uint)enumValue;
            if( valueType == typeof(long) ) return (ulong)(long)enumValue;
            if( valueType == typeof(ulong) ) return (ulong)enumValue;
            if( valueType == typeof(short) ) return (ulong)(short)enumValue;
            if( valueType == typeof(ushort) ) return (ulong)(ushort)enumValue;
            if( valueType == typeof(byte) ) return (ulong)(byte)enumValue;
            if( valueType == typeof(sbyte) ) return (ulong)(sbyte)enumValue;
            throw new InvalidOperationException();
        }

        static object GetEnumValueFromBits( ulong bits, Type enumType )
        {
            if( enumType.IsEnum )
            {
                var valueType = Enum.GetUnderlyingType( enumType );
                if( valueType == typeof(int) ) return (int)bits;
                if( valueType == typeof(uint) ) return (uint)bits;
                if( valueType == typeof(long) ) return (long)bits;
                if( valueType == typeof(ulong) ) return bits;
                if( valueType == typeof(short) ) return (short)bits;
                if( valueType == typeof(ushort) ) return (ushort)bits;
                if( valueType == typeof(byte) ) return (byte)bits;
                if( valueType == typeof(sbyte) ) return (sbyte)bits;
            }
            throw new InvalidOperationException();
        }

        [ Flags ]
        public enum Filter : uint
        {
            Fields = 1,
            Properties = Fields << 1,
            PublicStatic = Properties << 1,
            NonPublicStatic = PublicStatic << 1,
            Public = NonPublicStatic << 1,
            NonPublic = Public << 1,
            Inherited = NonPublic << 1,
        }
        
        class TypeCache
        {
            public readonly MemberInfo[] FilteredMembers;
            public readonly (Type key, Type value, MethodInfo getKey, MethodInfo getValue)? AsDictionary;
            public readonly Type AsList;
            public readonly ( bool flags, Array values )? asEnum;
            readonly Type _type;
            readonly Filter _filter;

            public TypeCache( Type t, Filter filter )
            {
                _type = t;
                _filter = filter;
                FilteredMembers = GetAllMembers( _type ).Where( m => PassesFilter( _type, m ) ).ToArray();
                
                var generics = GetGenericsFromBaseType( _type, typeof(IDictionary<,>) );
                if( generics == null && typeof(IDictionary).IsAssignableFrom( _type ) )
                    AsDictionary = ( typeof(object), typeof(object), null, null );
                else if( generics != null )
                    AsDictionary = ( generics[ 0 ], generics[ 1 ], null, null );

                if( AsDictionary != null )
                {
                    ( Type key, Type value, _, _ ) = AsDictionary.Value;
                    // IDictionary.Keys
                    var getKey = _type.GetProperty(nameof(IDictionary.Keys), BindingFlags.Public | BindingFlags.Instance)?.GetMethod;
                    // IDictionary.Values
                    var getValue = _type.GetProperty(nameof(IDictionary.Values), BindingFlags.Public | BindingFlags.Instance)?.GetMethod;
                    AsDictionary = (key, value, getKey, getValue);
                }

                generics = GetGenericsFromBaseType( _type, typeof(IList<>) );
                if( generics == null && typeof(IList).IsAssignableFrom( _type ) )
                    AsList = typeof(object);
                else if( generics != null )
                    AsList = generics[ 0 ];

                if( _type.IsEnum )
                {
                    asEnum = ( _type.IsDefined(typeof(FlagsAttribute) ), Enum.GetValues( _type ) );
                }
            }
            
            static Type[] GetGenericsFromBaseType( Type impl, Type type )
            {
                Type t = impl.GetInterfaces()
                    .Where( i => i.IsGenericType )
                    .FirstOrDefault( i => i.GetGenericTypeDefinition() == type );
                return t?.GenericTypeArguments;
            }
        
            public object NewObject()
            {
                if( _type == typeof(string) )
                    return string.Empty;
                return _type.IsValueType ? Activator.CreateInstance( _type ) : _type.GetConstructor(Type.EmptyTypes)?.Invoke( null );
            }
            
            bool PassesFilter( Type classType, MemberInfo m )
            {
                if( ! ( m is FieldInfo || m is PropertyInfo ) )
                    return false;

                Filter memberFilter = 0;

                if( classType != m.DeclaringType )
                    memberFilter |= Filter.Inherited;

                if( m is FieldInfo fi )
                {
                    if( IsBackingField( fi ) )
                        return false;
                    
                    memberFilter |= Filter.Fields;
                    if( fi.IsPublic )
                    {
                        if( fi.IsStatic )
                            memberFilter |= Filter.PublicStatic;
                        else
                            memberFilter |= Filter.Public;
                    }
                    else
                    {
                        if( fi.IsStatic )
                            memberFilter |= Filter.NonPublicStatic;
                        else
                            memberFilter |= Filter.NonPublic;
                    }
                }

                if( m is PropertyInfo pi )
                {
                    memberFilter |= Filter.Properties;
                    var method = pi.GetMethod;
                    if( method == null || method.GetParameters().Length != 0 )
                        return false;
                    
                    if( method.IsPublic )
                    {
                        if( method.IsStatic )
                            memberFilter |= Filter.PublicStatic;
                        else
                            memberFilter |= Filter.Public;
                    }
                    else
                    {
                        if( method.IsStatic )
                            memberFilter |= Filter.NonPublicStatic;
                        else
                            memberFilter |= Filter.NonPublic;
                    }
                }

                return (memberFilter & _filter) == memberFilter;
            }
            
            static bool IsBackingField(FieldInfo fi)
            {
                if( ! fi.IsPrivate )
                    return false;

                if( fi.Name[ 0 ] != '<' || ! fi.Name.EndsWith( ">k__BackingField" ) )
                    return false;

                return fi.IsDefined(typeof(CompilerGeneratedAttribute), true );
            }
            
            /// <summary> Reflection doesn't provide private inherited fields for some reason, this resolves that issue </summary>
            static IEnumerable<MemberInfo> GetAllMembers( Type t )
            {
                while( t != null )
                {
                    foreach( MemberInfo member in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly) )
                    {
                        yield return member;
                    }

                    t = t.BaseType;
                }
            }
        }
    }
}