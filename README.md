# Coal Mine AR/VR Training App

VR/AR-Based Safety Training and Skill Assessment System for High-Risk Coal Mining Operations.

A single Android application containing two modes:
- **AR Mode** — recognizes real-world mining safety equipment and warning signs (via a printed/photographed reference image), shows information about each item, then asks a short question to test the trainee's understanding and tracks a live score.
- **VR Mode** — a phone-based VR walkthrough of a simulated mine environment where trainees practice responding to high-risk hazard scenarios (e.g. gas leaks, fire, equipment failure). Shown on the main menu as "Coming Soon."

---

## Project Status

🚧 **In development.** AR Mode is functional end-to-end: main menu, camera pass-through, image recognition, info panels, quiz questions, and a live score counter all work and have been tested on a physical device (Samsung Galaxy S24 FE). VR Mode, results screen, backend, and dashboard are not yet started.

---

## Tech Stack

| Layer | Technology |
|---|---|
| App (AR + VR) | Unity 6 (6000.5.10f1), C# |
| AR | AR Foundation, ARCore XR Plugin |
| VR | XR Interaction Toolkit, OpenXR |
| Backend (planned) | Node.js, Express, MongoDB Atlas |
| Dashboard (planned) | React + Vite |
| Version Control | Git, Git LFS (for large binary assets) |

---

## Project Structure

```
CoalMineVR/
├── Assets/
│   ├── ARImages/       # Reference images used for AR Image Target recognition
│   ├── Prefabs/        # Reusable objects (info panel Canvas, placement Cube)
│   ├── Scenes/         # Unity scenes (MainMenu, ARMode)
│   ├── Scripts/        # C# scripts (menu, AR/quiz logic)
│   └── Settings/       # URP render pipeline & project settings assets
├── Packages/           # Unity package dependencies
├── ProjectSettings/    # Unity project configuration
├── .gitignore          # Unity-specific ignore rules
└── .gitattributes      # Git LFS tracking rules for large files
```

---

## Setup Guide (For Team Members)

### 1. Install required tools
- [Git](https://git-scm.com)
- [Git LFS](https://git-lfs.com)
- [VS Code](https://code.visualstudio.com) with the **C# Dev Kit** extension
- [Unity Hub](https://unity.com/download)
- Unity Editor **6000.5.10f1** (install via Unity Hub), with the **Android Build Support** module (including SDK, NDK, OpenJDK)

### 2. Set up Git LFS (one-time, per machine)
```bash
git lfs install
```

### 3. Clone the repository
```bash
git clone https://github.com/bramhagulavani/coal-mine-training.git
```

### 4. Open the project in Unity
1. Open **Unity Hub → Projects → Add**
2. Select the cloned `CoalMineVR` folder
3. Open it — first-time asset import may take several minutes

### 5. Point Unity to VS Code
`Edit → Preferences → External Tools → External Script Editor → Visual Studio Code`

### 6. Verify setup
- `File → Build Profiles` should show **Android** as the active platform, with `MainMenu` as scene 0 and `ARMode` as scene 1
- `Window → Package Manager` should list **AR Foundation**, **ARCore XR Plugin**, **XR Interaction Toolkit**, **OpenXR Plugin**
- `Assets → Settings → Mobile_Renderer` should have an **AR Background Renderer Feature** listed (required for the camera feed to display — without it the screen shows solid yellow)

### 7. Running on a phone
- Phone must be **ARCore-supported** (check Google's official list if unsure)
- Enable **USB debugging** (Settings → About phone → tap Build number 7 times → Developer options → USB debugging)
- Connect via USB, tap **Allow** on the phone's debugging prompt
- In Build Profiles, select the phone under **Run Device**, click **Build And Run**

---

## Daily Git Workflow

```bash
# Before starting work
git pull

# After finishing work
git add .
git commit -m "clear description of what changed"
git push
```

**Avoid** multiple people editing the same scene file at the same time — this causes merge conflicts that are hard to resolve in Unity scene files.

---

## Current Features Implemented

- [x] Main menu with Start AR Mode and VR Mode (Coming Soon) buttons
- [x] AR camera pass-through (fixed via AR Background Renderer Feature on URP Renderer)
- [x] AR plane detection + tap-to-place object (early pipeline test)
- [x] AR Image Target recognition — six items: Safety Helmet, Gas Mask, Danger: Toxic Gas sign, Roof Instability sign, Escape Route sign, Multi Gas Detector
- [x] Floating info panel per recognized image, billboard-facing the camera for readability
- [x] Quiz layer: each item shows a question with two tappable answers after a short delay, marked correct/incorrect
- [x] Live on-screen score counter (Score: X/Y)
- [ ] Results screen summarizing session performance
- [ ] VR mine environment
- [ ] VR hazard scenario with gaze-based interaction
- [ ] Backend (Node.js + MongoDB)
- [ ] Trainer dashboard (React)

---

## Content Source

AR item content is based on a domain survey grounded in the SIH 2022 problem statement **NC737** (Coal India Limited), covering the equipment and warning signs real miners rely on underground.

---

## Team

| Name | Roll No. |
|---|---|
| Krushnansh Sanjay Meher | 1252030009 |
| Harshvardhan Shivaji Dhere | 1252030014 |
| Atharv Dilip Rahate | 1252030026 |
| Bramha Vinayak Gulavani | 12520068 |

**Guide:** Prof. Kalyani Ghuge — Department of CSE (AIML), Vishwakarma Institute of Technology