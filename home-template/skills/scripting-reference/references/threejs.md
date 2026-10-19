## 7. JavaScript for Web 3D

```javascript
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';

// Scene
const scene = new THREE.Scene();
scene.background = new THREE.Color(0xf0f0f0);

// Camera
const camera = new THREE.PerspectiveCamera(
    60,                                    // FOV
    window.innerWidth / window.innerHeight, // aspect
    0.1,                                   // near
    10000                                  // far
);
camera.position.set(50, 30, 50);
camera.lookAt(0, 0, 0);

// Renderer
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setPixelRatio(window.devicePixelRatio);
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.0;
document.body.appendChild(renderer.domElement);

// Controls
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.05;
controls.maxPolarAngle = Math.PI / 2;  // prevent going below ground

// Lighting
const ambientLight = new THREE.AmbientLight(0xffffff, 0.4);
scene.add(ambientLight);

const directionalLight = new THREE.DirectionalLight(0xffffff, 0.8);
directionalLight.position.set(50, 100, 50);
directionalLight.castShadow = true;
directionalLight.shadow.mapSize.set(2048, 2048);
directionalLight.shadow.camera.left = -100;
directionalLight.shadow.camera.right = 100;
directionalLight.shadow.camera.top = 100;
directionalLight.shadow.camera.bottom = -100;
scene.add(directionalLight);

// Ground plane
const groundGeo = new THREE.PlaneGeometry(1000, 1000);
const groundMat = new THREE.MeshStandardMaterial({ color: 0xcccccc });
const ground = new THREE.Mesh(groundGeo, groundMat);
ground.rotation.x = -Math.PI / 2;
ground.receiveShadow = true;
scene.add(ground);

// Grid helper
const grid = new THREE.GridHelper(200, 20);
scene.add(grid);

// Animation loop
function animate() {
    requestAnimationFrame(animate);
    controls.update();
    renderer.render(scene, camera);
}
animate();

// Responsive resize
window.addEventListener('resize', () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
});
```

```javascript
// glTF (standard for web 3D)
const gltfLoader = new GLTFLoader();
gltfLoader.load('building.glb', (gltf) => {
    const model = gltf.scene;
    model.traverse((child) => {
        if (child.isMesh) {
            child.castShadow = true;
            child.receiveShadow = true;
        }
    });
    scene.add(model);
});

// IFC via web-ifc-three
import { IFCLoader } from 'web-ifc-three/IFCLoader';
const ifcLoader = new IFCLoader();
ifcLoader.ifcManager.setWasmPath('wasm/');
ifcLoader.load('model.ifc', (ifcModel) => {
    scene.add(ifcModel);
});
```

### Raycasting for Selection

### Materials for AEC

```javascript
import { IfcViewerAPI } from 'web-ifc-viewer';

const viewer = new IfcViewerAPI({
    container: document.getElementById('viewer-container'),
    backgroundColor: new THREE.Color(0xffffff)
});
viewer.grid.setGrid();
viewer.axes.setAxes();

const model = await viewer.IFC.loadIfcUrl('model.ifc');
viewer.shadowDropper.renderShadow(model.modelID);

// Spatial tree
const tree = await viewer.IFC.getSpatialStructure(model.modelID);

// Selection
viewer.IFC.selector.prePickIfcItem();  // highlight on hover
viewer.IFC.selector.pickIfcItem();      // select on click

// Properties
const props = await viewer.IFC.getProperties(model.modelID, expressID, true);
```
