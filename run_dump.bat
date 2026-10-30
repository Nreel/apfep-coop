@echo off
set DOTNET_ROLL_FORWARD=Major
cd "L:\agent test space\apfep-coop\tools\Il2CppDumper"
Il2CppDumper.exe "..\install\GameAssembly.dll" "..\install\A Place for Every Potion_Data\il2cpp_data\Metadata\global-metadata.dat" "..\dump"
