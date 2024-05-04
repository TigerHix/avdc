# Automatic Virtual Dance Cinematography

![Method comparison](https://github.com/TigerHix/avdc/blob/master/images/header.png?raw=true)

## Setup

Download the [dataset](https://drive.google.com/drive/folders/1XE4gfY07beweOt6k6DqU5ErVRVNWKt0D?usp=sharing) and put it into a folder named `dataset`. Please view the Jupyter Notebook for details.

## Unity components

The `unity` folder contains playground C#/Unity scripts for [Warudo](https://warudo.app/), a 3D avatar animation software. Data annotators installed the `MMDDataFilterer.cs` to synchronize camera movements with audio and character movements as well as filtering out incomplete/invalid camera animation files. Then, we use the `MMDDataProcessor.cs` to export the annotated MMD data into JSON files which will be imported into the Jupyter Notebook as NumPy arrays (the dataset above).
