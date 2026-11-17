using System.Collections.Generic;

namespace Foreman
{
	//A single surface condition as factorio defines it: a named surface property plus an optional
	//minimum and maximum. Absent bounds are stored as infinities so every check is just a range test.
	//Equality conditions (min == max) are the common case in space age - 'pressure 2000..2000' is how
	//a recipe says 'gleba only'.
	public struct SurfaceCondition
	{
		//shared empty set - almost every recipe and entity has no conditions, so they all point here
		//rather than allocating thousands of empty lists.
		public static readonly IReadOnlyList<SurfaceCondition> None = new SurfaceCondition[0];

		public string Property { get; }
		public double Min { get; }
		public double Max { get; }

		public SurfaceCondition(string property, double min, double max)
		{
			Property = property;
			Min = min;
			Max = max;
		}

		public bool Contains(double value) { return value >= Min && value <= Max; }

		public override string ToString()
		{
			if (Min == Max)
				return string.Format("{0} = {1}", Property, Min);
			if (double.IsNegativeInfinity(Min))
				return string.Format("{0} <= {1}", Property, Max);
			if (double.IsPositiveInfinity(Max))
				return string.Format("{0} >= {1}", Property, Min);
			return string.Format("{0} {1}..{2}", Property, Min, Max);
		}
	}

	//A surface property prototype (gravity, pressure, magnetic-field, ...). The default value matters:
	//a planet lists only the properties it differs on, so every property a planet does not mention is
	//read from here. In the base game the defaults are nauvis' values, which is why a nauvis-only
	//recipe can be written as 'pressure 1000..1000' and still pass on nauvis.
	public class SurfaceProperty
	{
		public string Name { get; }
		public string FriendlyName { get; }
		public double DefaultValue { get; }
		public bool IsTime { get; }

		public SurfaceProperty(string name, string friendlyName, double defaultValue, bool isTime)
		{
			Name = name;
			FriendlyName = friendlyName;
			DefaultValue = defaultValue;
			IsTime = isTime;
		}

		public override string ToString() { return string.Format("SurfaceProperty: {0} (default {1})", Name, DefaultValue); }
	}

	public interface Surface : DataObjectBase
	{
		//'planet' for a planet prototype, 'surface' for a surface prototype (the space platform). Kept as
		//the raw prototype type rather than an enum so a modded surface type passes through untouched.
		string SurfaceType { get; }

		//only the properties this surface overrides; anything else comes from the property's default.
		IReadOnlyDictionary<string, double> PropertyOverrides { get; }

		double GetPropertyValue(string property);
		bool Passes(IReadOnlyList<SurfaceCondition> conditions);
	}

	public class SurfacePrototype : DataObjectBasePrototype, Surface
	{
		public string SurfaceType { get; internal set; }

		public IReadOnlyDictionary<string, double> PropertyOverrides { get { return propertyOverrides; } }

		internal Dictionary<string, double> propertyOverrides { get; private set; }

		public SurfacePrototype(DataCache dCache, string name, string friendlyName, string order, string surfaceType) : base(dCache, name, friendlyName, order)
		{
			SurfaceType = surfaceType;
			propertyOverrides = new Dictionary<string, double>();
		}

		public double GetPropertyValue(string property)
		{
			if (propertyOverrides.TryGetValue(property, out double value))
				return value;
			if (Owner.SurfaceProperties.TryGetValue(property, out SurfaceProperty sProperty))
				return sProperty.DefaultValue;
			return 0;
		}

		//An empty or null condition set passes everywhere - which is almost every recipe in every preset.
		//A condition naming a property this preset never exported also passes: a mod can add a property
		//Foreman knows nothing about, and failing closed there would silently empty the recipe list rather
		//than just being a little permissive.
		public bool Passes(IReadOnlyList<SurfaceCondition> conditions)
		{
			if (conditions == null)
				return true;

			for (int i = 0; i < conditions.Count; i++)
			{
				if (!Owner.SurfaceProperties.ContainsKey(conditions[i].Property) && !propertyOverrides.ContainsKey(conditions[i].Property))
					continue;
				if (!conditions[i].Contains(GetPropertyValue(conditions[i].Property)))
					return false;
			}
			return true;
		}

		public override string ToString() { return string.Format("Surface ({0}): {1}", SurfaceType, Name); }
	}
}
