﻿using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.Models.Runs.Factory;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   public enum ElementContentType {
      Unknown,
      PCS,
      Integer,
      Pointer,
      BitArray,
   }

   public interface IHasOptions {
      IEnumerable<string> GetOptions(IDataModel model);
   }

   public class ArrayRunElementSegment {
      public string Name { get; }
      public ElementContentType Type { get; }
      public int Length { get; }
      public virtual string SerializeFormat {
         get {
            if (Type == ElementContentType.PCS) return $"{Name}\"\"{Length}";
            if (Type == ElementContentType.Pointer) return $"{Name}<>";
            if (Type == ElementContentType.Integer && Length == 1) return $"{Name}.";
            if (Type == ElementContentType.Integer && Length == 2) return $"{Name}:";
            if (Type == ElementContentType.Integer && Length == 3) return $"{Name}:.";
            if (Type == ElementContentType.Integer && Length == 4) return $"{Name}::";
            throw new NotImplementedException();
         }
      }

      public ArrayRunElementSegment(string name, ElementContentType type, int length) => (Name, Type, Length) = (name, type, length);

      private bool recursionStopper;
      public virtual string ToText(IDataModel rawData, int offset, bool deep = false) {
         switch (Type) {
            case ElementContentType.PCS:
               return PCSString.Convert(rawData, offset, Length);
            case ElementContentType.Integer:
               return ToInteger(rawData, offset, Length).ToString();
            case ElementContentType.Pointer:
               var address = rawData.ReadPointer(offset);
               var anchor = rawData.GetAnchorFromAddress(-1, address);
               if (string.IsNullOrEmpty(anchor)) anchor = address.ToString("X6");
               var run = rawData.GetNextRun(address) as IAppendToBuilderRun;
               if (!deep || recursionStopper || run == null) return $"{PointerRun.PointerStart}{anchor}{PointerRun.PointerEnd}";

               var builder = new StringBuilder("@{ ");
               recursionStopper = true;
               run.AppendTo(rawData, builder, run.Start, run.Length, deep);
               recursionStopper = false;
               builder.Append(" @} ");

               return builder.ToString();
            default:
               throw new NotImplementedException();
         }
      }

      public virtual void Write(IDataModel model, ModelDelta token, int start, string data) {
         switch (Type) {
            case ElementContentType.PCS:
               var bytes = PCSString.Convert(data);
               while (bytes.Count > Length) bytes.RemoveAt(bytes.Count - 1);
               if (!bytes.Contains(0xFF)) bytes[bytes.Count - 1] = 0xFF;
               while (bytes.Count < Length) bytes.Add(0);
               for (int i = 0; i < Length; i++) token.ChangeData(model, start + i, bytes[i]);
               break;
            case ElementContentType.Integer:
               if (!int.TryParse(data, out var intValue)) intValue = 0;
               if (model.ReadMultiByteValue(start, Length) != intValue) {
                  model.WriteMultiByteValue(start, Length, token, intValue);
               }
               break;
            case ElementContentType.Pointer:
               data = data.Trim();
               if (data.StartsWith("<")) data = data.Substring(1);
               if (data.EndsWith(">")) data = data.Substring(0, data.Length - 1);
               if (!int.TryParse(data, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var address)) {
                  address = model.GetAddressFromAnchor(token, -1, data);
               }
               if (model.ReadPointer(start) != address) {
                  model.WritePointer(token, start, address);
               }
               break;
            default:
               throw new NotImplementedException();
         }
      }

      public static int ToInteger(IReadOnlyList<byte> data, int offset, int length) {
         int result = 0;
         int shift = 0;
         for (int i = 0; i < length; i++) {
            result += data[offset + i] << shift;
            shift += 8;
         }
         return result;
      }
   }

   public class ArrayRunEnumSegment : ArrayRunElementSegment, IHasOptions {
      public string EnumName { get; }

      public int ValueOffset { get; }

      public override string SerializeFormat => base.SerializeFormat + EnumName;

      public ArrayRunEnumSegment(string name, int length, string enumName) : base(name, ElementContentType.Integer, length) {
         EnumName = enumName;
         var parts = EnumName.Split("+");
         if (parts.Length == 2 && int.TryParse(parts[1], out int valueOffset)) {
            EnumName = parts[0];
            ValueOffset = valueOffset;
         }
      }

      public override string ToText(IDataModel model, int address, bool deep) {
         using (ModelCacheScope.CreateScope(model)) {
            var options = GetOptions(model).ToList();
            if (options == null) return base.ToText(model, address, deep);

            var resultAsInteger = ToInteger(model, address, Length) - ValueOffset;
            if (resultAsInteger >= options.Count || resultAsInteger < 0) return base.ToText(model, address, deep);
            var value = options[resultAsInteger];

            // use ~2 postfix for a value if an earlier entry in the array has the same string
            var elementsUpToHereWithThisName = 1;
            for (int i = resultAsInteger - 1; i >= 0; i--) {
               var previousValue = options[i];
               if (previousValue == value) elementsUpToHereWithThisName++;
            }
            if (value.StartsWith("\"") && value.EndsWith("\"")) value = value.Substring(1, value.Length - 2);
            if (elementsUpToHereWithThisName > 1) value += "~" + elementsUpToHereWithThisName;

            // add quotes around it if it contains a space
            if (value.Contains(' ')) value = $"\"{value}\"";

            return value;
         }
      }

      // TODO do some sort of caching: rendering these images every time probably sucks for performance.
      public IEnumerable<ComboOption> GetComboOptions(IDataModel model) {
         var defaultOptions = GetOptions(model).Select((option, i) => new ComboOption(option, i)).ToList();
         if (!(model.GetNextRun(model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, EnumName)) is ITableRun tableRun)) return defaultOptions;
         if (!(tableRun.ElementContent[0] is ArrayRunPointerSegment pointerSegment)) return defaultOptions;
         if (!LzSpriteRun.TryParseSpriteFormat(pointerSegment.InnerFormat, out var _) && !SpriteRun.TryParseSpriteFormat(pointerSegment.InnerFormat, out var _)) return defaultOptions;

         var imageOptions = new List<ComboOption>();
         for (int i = 0; i < tableRun.ElementCount; i++) {
            var destination = model.ReadPointer(tableRun.Start + tableRun.ElementLength * i);
            if (!(model.GetNextRun(destination) is ISpriteRun run)) return defaultOptions;
            var sprite = run.GetPixels(model, 0);
            var paletteAddress = SpriteTool.FindMatchingPalette(model, run, 0);
            var paletteRun = model.GetNextRun(paletteAddress) as IPaletteRun;
            var palette = paletteRun?.GetPalette(model, paletteRun.PaletteFormat.InitialBlankPages) ?? TileViewModel.CreateDefaultPalette(16);
            var image = SpriteTool.Render(sprite, palette, paletteRun?.PaletteFormat.InitialBlankPages ?? default, 0);
            var option = VisualComboOption.CreateFromSprite(defaultOptions[i].Text, image, sprite.GetLength(0), i);
            imageOptions.Add(option);
         }

         return imageOptions;
      }

      public override void Write(IDataModel model, ModelDelta token, int start, string data) {
         using (ModelCacheScope.CreateScope(model)) {
            if (!TryParse(model, data, out int value)) {
               base.Write(model, token, start, data);
               return;
            }

            base.Write(model, token, start, value.ToString());
         }
      }

      public bool TryParse(IDataModel model, string text, out int value) {
         var result = TryParse(EnumName, model, text, out value);
         value += ValueOffset;
         return result;
      }

      public static bool TryParse(string enumName, IDataModel model, string text, out int value) {
         var options = model.GetOptions(enumName);
         return TryMatch(text, options, out value);
      }

      public static bool TryMatch(string text, IReadOnlyList<string> list, out int value) {
         text = text.Trim();
         if (int.TryParse(text, out value)) return true;
         if (text.StartsWith("\"") && text.EndsWith("\"")) text = text.Substring(1, text.Length - 2);
         var partialMatches = new List<string>();
         var matches = new List<string>();

         // if the ~ character is used, expect that it's saying which match we want
         var desiredMatch = 1;
         var splitIndex = text.IndexOf('~');
         if (splitIndex != -1 && !int.TryParse(text.Substring(splitIndex + 1), out desiredMatch)) return false;
         if (splitIndex != -1) text = text.Substring(0, splitIndex);

         // ok, so everything lines up... check the array to see if any values match the string entered
         text = text.ToLower();
         var options = list;
         for (int i = 0; i < options.Count; i++) {
            var option = (options[i] ?? i.ToString()).ToLower().Split("~")[0];
            if (option.StartsWith("\"") && option.EndsWith("\"")) option = option.Substring(1, option.Length - 2);
            if (option == text) matches.Add(option);
            if (matches.Count == desiredMatch) { value = i; return true; }
            if (option.MatchesPartial(text)) {
               partialMatches.Add(option);
               if (partialMatches.Count == desiredMatch && matches.Count == 0) value = i;
            }
         }
         if (matches.Count == 0 && partialMatches.Count >= desiredMatch) return true; // no full matches, use the partial match

         // we went through the whole array and didn't find it :(
         return false;
      }

      public IEnumerable<string> GetOptions(IDataModel model) {
         if (int.TryParse(EnumName, out var result)) return result.Range().Select(i => i.ToString());
         IEnumerable<string> options = model.GetOptions(EnumName);

         // we _need_ options for the table tool
         // if we have none, just create "0", "1", ..., "n-1" based on the length of the EnumName table.
         if (!options.Any()) {
            if (model.GetNextRun(model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, EnumName)) is ITableRun tableRun) {
               options = tableRun.ElementCount.Range().Select(i => i.ToString());
            }
         }

         return options;
      }
   }

   public class ArrayRunRecordSegment : ArrayRunElementSegment {
      public string MatchField { get; }
      public IReadOnlyDictionary<int,string> EnumForValue { get; }

      public override string SerializeFormat {
         get {
            var records = "|".Join(EnumForValue.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
            return $"{base.SerializeFormat}|s={MatchField}({records})";
         }
      }

      public ArrayRunRecordSegment(string name, int length, string recordSwitch) : base(name, ElementContentType.Integer, length) {
         Debug.Assert(recordSwitch.StartsWith("|s="));
         Debug.Assert(recordSwitch.Contains("(") && recordSwitch.Contains(")"));
         recordSwitch = recordSwitch.Substring(3);
         MatchField = recordSwitch.Split("(")[0];
         if (string.IsNullOrEmpty(MatchField)) throw new ArrayRunParseException("Record format is s={name}({number}={enum}|...).");
         recordSwitch = recordSwitch.Substring(MatchField.Length + 1).Replace(")", string.Empty);

         var enumForValue = new Dictionary<int, string>();
         foreach (var pair in recordSwitch.Split("|")) {
            var keyAndValue = pair.Split("=");
            if (keyAndValue.Length != 2) continue;
            if (!int.TryParse(keyAndValue[0], out int key)) continue;
            enumForValue[key] = keyAndValue[1];
         }
         EnumForValue = enumForValue;
      }

      public ArrayRunElementSegment CreateConcrete(IDataModel model, int offset) {
         var defaultConcrete = new ArrayRunElementSegment(Name, ElementContentType.Integer, Length);
         var table = (ITableRun)model.GetNextRun(offset);
         int matchFieldOffset = 0;
         int matchFieldIndex = 0;
         for (int i = 0; i < table.ElementContent.Count; i++) {
            if (table.ElementContent[i].Name == MatchField) break;
            matchFieldOffset += table.ElementContent[i].Length;
            matchFieldIndex += 1;
         }
         if (matchFieldOffset == table.ElementLength) return defaultConcrete;
         var offsets = table.ConvertByteOffsetToArrayOffset(offset);
         var matchFieldValue = model.ReadMultiByteValue(table.Start + offsets.ElementIndex * table.ElementLength + matchFieldOffset, table.ElementContent[matchFieldIndex].Length);
         if (!EnumForValue.TryGetValue(matchFieldValue, out var enumName)) return defaultConcrete;
         return new ArrayRunEnumSegment(Name, Length, enumName);
      }
   }

   public class ArrayRunHexSegment : ArrayRunElementSegment {
      public override string SerializeFormat => base.SerializeFormat + ArrayRun.HexFormatString;

      public ArrayRunHexSegment(string name, int length) : base(name, ElementContentType.Integer, length) {
      }

      public override string ToText(IDataModel rawData, int offset, bool deep = false) {
         var hexLength = "X" + (Length * 2);
         var hex = rawData.ReadMultiByteValue(offset, Length).ToString(hexLength);
         return hex;
      }

      public override void Write(IDataModel model, ModelDelta token, int start, string data) {
         if (!int.TryParse(data, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var intValue)) intValue = 0;
         model.WriteMultiByteValue(start, Length, token, intValue);
      }
   }

   public class ArrayRunColorSegment : ArrayRunElementSegment {
      public override string SerializeFormat => base.SerializeFormat + ArrayRun.ColorFormatString;

      public ArrayRunColorSegment(string name) : base(name, ElementContentType.Integer, 2) { }

      public override string ToText(IDataModel rawData, int offset, bool deep = false) {
         var color = (short)rawData.ReadMultiByteValue(offset, Length);
         var colorText = UncompressedPaletteColor.Convert(color);
         return colorText;
      }
   }

   public class ArrayRunBitArraySegment : ArrayRunElementSegment, IHasOptions {
      public string SourceArrayName { get; }

      public override string SerializeFormat => $"{Name}{BitArray.SharedFormatString}{SourceArrayName}";

      public ArrayRunBitArraySegment(string name, int length, string bitSourceName) : base(name, ElementContentType.BitArray, length) {
         SourceArrayName = bitSourceName;
      }

      public override string ToText(IDataModel rawData, int offset, bool deep) {
         var result = new StringBuilder("-");
         var options = rawData.GetBitOptions(SourceArrayName);

         for (int i = 0; i < Length; i++) {
            var bits = rawData[offset + i];
            var optionOffset = i << 3;
            for (int j = 0; j < 8; j++) {
               if ((bits & (1 << j)) == 0) continue;
               result.Append(" ");
               result.Append(options[optionOffset + j]);
            }
         }

         result.Append(" /");
         return result.ToString();
      }

      public override void Write(IDataModel model, ModelDelta token, int start, string data) {
         for (int i = 0; i < Length && i * 2 + 1 < data.Length; i++) {
            if (!byte.TryParse(data.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var value)) value = 0;
            token.ChangeData(model, start + i, value);
         }
      }

      public IEnumerable<string> GetOptions(IDataModel model) {
         var cache = ModelCacheScope.GetCache(model);
         return cache.GetBitOptions(SourceArrayName);
      }
   }

   /// <summary>
   /// For pointers that contain nested formatting instructions.
   /// For example, pointing to a text stream or a plm (pokemon learnable moves) stream
   /// </summary>
   public class ArrayRunPointerSegment : ArrayRunElementSegment {
      public string InnerFormat { get; }

      public bool IsInnerFormatValid => FormatRunFactory.GetStrategy(InnerFormat) != null;

      public override string SerializeFormat => $"{Name}<{InnerFormat}>";

      public ArrayRunPointerSegment(string name, string innerFormat) : base(name, ElementContentType.Pointer, 4) {
         InnerFormat = innerFormat;
      }

      public bool DestinationDataMatchesPointerFormat(IDataModel owner, ModelDelta token, int source, int destination, IReadOnlyList<ArrayRunElementSegment> sourceSegments, int parentIndex) {
         if (destination == Pointer.NULL) return true;
         var run = owner.GetNextAnchor(destination);
         if (run.Start < destination) return false;
         if (run.Start > destination || (run.Start == destination && (run is NoInfoRun || run is PointerRun))) {
            // hard case: no format found, so check the data
            if (destination < 0 || destination >= owner.Count) return false;
            return FormatRunFactory.GetStrategy(InnerFormat)?.TryAddFormatAtDestination(owner, token, source, destination, Name, sourceSegments, parentIndex) ?? false;
         } else {
            // easy case: already have a format, just see if it matches
            var strategy = FormatRunFactory.GetStrategy(InnerFormat);
            if (strategy == null) return false;
            if (strategy.Matches(run)) return true;

            // special test: the format of the data is wrong?
            return strategy.TryAddFormatAtDestination(owner, token, source, destination, Name, sourceSegments, parentIndex);
         }
      }

      public void WriteNewFormat(IDataModel owner, ModelDelta token, int source, int destination, IReadOnlyList<ArrayRunElementSegment> sourceSegments) {
         owner.WritePointer(token, source, destination);
         var newRun = FormatRunFactory.GetStrategy(InnerFormat).WriteNewRun(owner, token, source, destination, Name, sourceSegments);
         owner.ObserveRunWritten(token, newRun.MergeAnchor(new SortedSpan<int>(source)));
      }
   }
}
