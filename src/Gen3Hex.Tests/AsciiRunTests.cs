﻿using HavenSoft.Gen3Hex.Core.Models;
using HavenSoft.Gen3Hex.Core.Models.Runs;
using HavenSoft.Gen3Hex.Core.ViewModels;
using HavenSoft.Gen3Hex.Core.ViewModels.DataFormats;
using Xunit;

namespace HavenSoft.Gen3Hex.Tests {
   public class AsciiRunTests {
      [Fact]
      public void CanCreateAsciiRun() {
         var buffer = new byte[0x200];
         var model = new PokemonModel(buffer);
         model[0x10] = (byte)'a';
         model[0x11] = (byte)'b';
         model[0x12] = (byte)'c';
         model[0x13] = (byte)'d';

         var viewPort = new ViewPort(new LoadedFile("file.txt", buffer), model) { Width = 0x10, Height = 0x10 };
         viewPort.SelectionStart = new Point(0, 1);
         viewPort.Edit("^data`asc`4 ");

         var format = (Ascii)viewPort[1, 1].Format;
         Assert.Equal('b', format.ThisCharacter);
      }

      [Fact]
      public void CanEditAsciiRun() {
         var buffer = new byte[0x200];
         var model = new PokemonModel(buffer);
         model[0x10] = (byte)'a';
         model[0x11] = (byte)'b';
         model[0x12] = (byte)'c';
         model[0x13] = (byte)'d';

         model.ObserveRunWritten(new ModelDelta(), new AsciiRun(0x10, 4));

         var viewPort = new ViewPort(new LoadedFile("file.txt", buffer), model) { Width = 0x10, Height = 0x10 };
         viewPort.SelectionStart = new Point(1, 1);
         viewPort.Edit("3");

         var format = (Ascii)viewPort[1, 1].Format;
         Assert.Equal('3', format.ThisCharacter);
         Assert.Equal(new Point(2, 1), viewPort.SelectionStart);
      }
   }
}
