# OSM City Builder for Unity

OSM City Builder is a Unity Editor tool that imports OpenStreetMap data through the Overpass API and generates a procedural 3D city layout inside the Unity Editor.

It is aimed at indie developers who want a fast way to prototype city scenes without manually placing every building.

## Features

- Import building footprints from OpenStreetMap
- Generate extruded 3D buildings from OSM geometry
- Use building heights from OSM tags when available
- Generate roads and simple point objects
- Choose the import area by circle radius or bounding box
- Save meshes as Unity assets
- Save materials as Unity assets
- Combine buildings into chunks or a single mesh
- Per-building texture overrides
- Editor-only workflow

## Requirements

- Unity 2021 LTS or newer
- Internet access for Overpass API requests

## Installation

1. Copy the `Assets/Editor/OSMCityBuilderWindow.cs` file into your Unity project.
2. Open Unity and wait for the script to compile.
3. Open **Tools → OSM City Builder**.
4. Enter coordinates or bounds and generate the city.

## Notes

- The quality of the result depends on the OpenStreetMap data available for the selected area.
- This is an early open-source release, so some object types may still be simplified.
- Large areas can take time to generate and may create many objects.

## Roadmap

- Better road meshes
- More object types
- Terrain support
- Improved roof types
- Better material tagging
- LOD generation
- Optional satellite-style base textures

## License

MIT License
