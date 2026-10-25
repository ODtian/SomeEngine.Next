import * as THREE from 'three';
import { OrbitControls } from './vendor/OrbitControls.js';

const element = id => document.getElementById(id);
const viewport = element('viewport');
const garmentLabels = [...element('garment-labels').children];
const labelPosition = new THREE.Vector3();
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
viewport.append(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color('#141d27');
scene.fog = new THREE.Fog('#141d27', 7, 14);
const perspectiveCamera = new THREE.PerspectiveCamera(32, 1, .02, 30);
const sideCamera = new THREE.OrthographicCamera(-.3, .3, .18, -.18, .02, 30);
let camera = perspectiveCamera;
const orbit = new OrbitControls(camera, renderer.domElement);
orbit.enableDamping = true;
orbit.dampingFactor = .09;
orbit.minDistance = .5;
orbit.maxDistance = 7;
orbit.maxPolarAngle = Math.PI * .85;
camera.position.set(.85, 1.12, 3.25);
orbit.target.set(0, .85, 0);
scene.add(new THREE.HemisphereLight(0xdcecff, 0x788477, 2.2));
const keyLight = new THREE.DirectionalLight(0xfff0df, 3.1);
keyLight.position.set(-2, 4, 3);
keyLight.castShadow = true;
keyLight.shadow.mapSize.set(1536, 1536);
Object.assign(keyLight.shadow.camera, { left: -2.5, right: 2.5, top: 2.5, bottom: -2.5, near: .2, far: 10 });
keyLight.shadow.bias = -.0005;
keyLight.shadow.normalBias = .006;
scene.add(keyLight);
const rimLight = new THREE.DirectionalLight(0x8dc9ee, 2.1);
rimLight.position.set(2, 2, -3);
scene.add(rimLight);
const floor = new THREE.Mesh(new THREE.PlaneGeometry(50, 50), new THREE.MeshStandardMaterial({ color: 0x18222b, roughness: .95 }));
floor.rotation.x = -Math.PI / 2;
floor.position.y = -.006;
floor.receiveShadow = true;
scene.add(floor);
const floorGrid = new THREE.GridHelper(6, 30, 0x324352, 0x23313e);
floorGrid.position.y = -.004;
floorGrid.material.transparent = true;
floorGrid.material.opacity = .38;
scene.add(floorGrid);

const controls = { shellOnly: true, briefs: 0, tights: 0, running: 0, spread: 4 * Math.PI / 180, wind: 0,
  dualQuaternion: true, fine: false, paused: false, reset: false };
let description;
let bodyGeometry;
let trackGeometry;
let shellGeometry;
let shells = [];
let shellWires = [];
let progressRings = [];
let progressGeometry;
let seams = [];
let tracks = [];
let bodies = [];
let garments = [];
let wires = [];
let figures = [];
let heads = [];
let garmentView = 'tights';
let cameraView = 'front';
let colorMode = 'fabric';
let readyToSend = false;
let lastUiFrame = 0;
let timings = [];
let latestFrame;
let previousFrameTime = performance.now();
let elapsedSeconds = 0;
let redraw = true;
const attachedColor = new THREE.Color('#64d6bc');
const simulatedColor = new THREE.Color('#f59b72');
const clothColors = [new THREE.Color('#407b70'), new THREE.Color('#30333f')];
const color = new THREE.Color();

function geometry(vertexCount, triangles) {
  const mesh = new THREE.BufferGeometry();
  mesh.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vertexCount * 3), 3).setUsage(THREE.DynamicDrawUsage));
  mesh.setIndex(triangles);
  return mesh;
}

function build(meta) {
  description = meta;
  element('quality').options[0].textContent = `实时 · ${meta.substeps} 子步 × ${meta.solverIterations} 轮`;
  element('quality').options[1].textContent = `对照 · ${meta.substeps} 子步 × ${meta.referenceIterations} 轮`;
  bodyGeometry = geometry(meta.bodyVertexMap.length, meta.bodyTriangles);
  bodyGeometry.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(meta.bodyVertexMap.length * 3), 3).setUsage(THREE.DynamicDrawUsage));
  bodyGeometry.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(meta.bodyTextureCoordinates), 2));
  for (const part of meta.bodyMaterials) bodyGeometry.addGroup(part.start, part.count, part.materialIndex);
  const textureLoader = new THREE.TextureLoader();
  const loadColor = name => {
    const texture = textureLoader.load(`./character/${name}.png`);
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = renderer.capabilities.getMaxAnisotropy();
    return texture;
  };
  const skin = new THREE.MeshPhysicalMaterial({ map: loadColor('skin'), roughness: .64, sheen: .08, sheenRoughness: .8 });
  const top = new THREE.MeshStandardMaterial({ map: loadColor('sportstop'), normalMap: textureLoader.load('./character/sportstop-normal.png'), roughness: .9, side: THREE.DoubleSide });
  const hairMaterial = new THREE.MeshStandardMaterial({ map: loadColor('hair'), color: 0x8c6957, roughness: .65, alphaTest: .38, side: THREE.DoubleSide });
  const eyeMaterial = new THREE.MeshPhysicalMaterial({ map: loadColor('eyes'), roughness: .22, clearcoat: .4 });
  const accessory = (data, material) => {
    const surface = new THREE.BufferGeometry();
    surface.setAttribute('position', new THREE.Float32BufferAttribute(data.positions, 3));
    surface.setAttribute('normal', new THREE.Float32BufferAttribute(data.normals, 3));
    surface.setAttribute('uv', new THREE.Float32BufferAttribute(data.textureCoordinates, 2));
    surface.setIndex(data.triangles);
    const mesh = new THREE.Mesh(surface, material);
    mesh.castShadow = true; mesh.receiveShadow = true;
    return mesh;
  };
  trackGeometry = geometry(meta.trajectoryVertices, []);
  const trackEdges = [];
  for (let surface = 0; surface < meta.trajectorySurfaces; surface++) {
    for (let row = 0; row < meta.trajectoryRows; row++) for (let column = 0; column < meta.trajectoryColumns; column++) {
      const point = (surface * meta.trajectoryRows + row) * meta.trajectoryColumns + column;
      if (row % 4 === 0 && (surface < 2 || column + 1 < meta.trajectoryColumns)) trackEdges.push(point, (surface * meta.trajectoryRows + row) * meta.trajectoryColumns + (column + 1) % meta.trajectoryColumns);
      if (column % 2 === 0 && row + 1 < meta.trajectoryRows) trackEdges.push(point, point + meta.trajectoryColumns);
    }
  }
  trackGeometry.setIndex(trackEdges);
  const trackColors = new Float32Array(meta.trajectoryVertices * 3);
  meta.trajectoryWeights.forEach((weight, index) => color.copy(attachedColor).lerp(simulatedColor, weight).toArray(trackColors, index * 3));
  trackGeometry.setAttribute('color', new THREE.BufferAttribute(trackColors, 3));
  shellGeometry = geometry(meta.shell.vertices, meta.shell.triangles);
  const shellEdges = new THREE.BufferGeometry();
  shellEdges.setAttribute('position', shellGeometry.attributes.position);
  shellEdges.setIndex(meta.shell.transportEdges);
  const shellSections = new THREE.BufferGeometry();
  shellSections.setAttribute('position', shellGeometry.attributes.position);
  shellSections.setIndex(meta.shell.sectionEdges);
  progressGeometry = new THREE.BufferGeometry();
  progressGeometry.setAttribute('position', shellGeometry.attributes.position);
  [meta.briefs, meta.tights].forEach((garment, index) => {
    const group = new THREE.Group();
    group.position.x = 0;
    group.visible = index === 1;
    scene.add(group);
    figures.push(group);
    const body = new THREE.Mesh(bodyGeometry, [skin, top]);
    body.castShadow = true;
    body.receiveShadow = true;
    group.add(body);
    bodies.push(body);
    const head = new THREE.Group();
    head.add(accessory(meta.hair, hairMaterial), accessory(meta.eyes, eyeMaterial));
    group.add(head);
    heads.push(head);
    const clothGeometry = geometry(garment.vertices, garment.triangles);
    clothGeometry.setAttribute('color', new THREE.BufferAttribute(new Float32Array(garment.vertices * 3).fill(1), 3).setUsage(THREE.DynamicDrawUsage));
    const cloth = new THREE.Mesh(clothGeometry, new THREE.MeshPhysicalMaterial({ color: 0xffffff, vertexColors: true,
      roughness: .76, sheen: .65, sheenRoughness: .8, sheenColor: new THREE.Color('#8d97a4'), side: THREE.DoubleSide }));
    cloth.castShadow = true;
    cloth.receiveShadow = true;
    group.add(cloth);
    garments.push(cloth);
    const boundaryGeometry = new THREE.BufferGeometry();
    boundaryGeometry.setAttribute('position', clothGeometry.attributes.position);
    boundaryGeometry.setIndex(garment.boundaryEdges);
    const seam = new THREE.LineSegments(boundaryGeometry, new THREE.LineBasicMaterial({ color: index ? 0x747988 : 0x83afa4, transparent: true, opacity: .45 }));
    seam.frustumCulled = false;
    group.add(seam);
    seams.push(seam);
    const wire = new THREE.Mesh(clothGeometry, new THREE.MeshBasicMaterial({ color: 0xe0edf4, wireframe: true, transparent: true, opacity: .25 }));
    wire.visible = false;
    group.add(wire);
    wires.push(wire);
    const track = new THREE.LineSegments(trackGeometry, new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: .45, depthTest: false }));
    track.visible = false;
    group.add(track);
    tracks.push(track);
    const shell = new THREE.Mesh(shellGeometry, new THREE.MeshStandardMaterial({ color: 0x42b7a5, roughness: .7, side: THREE.DoubleSide,
      polygonOffset: true, polygonOffsetFactor: 0, polygonOffsetUnits: 1, depthWrite: true }));
    shell.renderOrder = 1;
    group.add(shell);
    shells.push(shell);
    const shellWire = new THREE.LineSegments(shellEdges, new THREE.LineBasicMaterial({ color: 0xffdb82, transparent: true, opacity: .85 }));
    shellWire.renderOrder = 2;
    group.add(shellWire);
    shellWires.push(shellWire);
    const shellSection = new THREE.LineSegments(shellSections, new THREE.LineBasicMaterial({ color: 0x123e3a, transparent: true, opacity: .65 }));
    shellSection.renderOrder = 2;
    group.add(shellSection);
    shellWires.push(shellSection);
    const progressRing = new THREE.LineSegments(progressGeometry, new THREE.LineBasicMaterial({ color: 0xff785e }));
    progressRing.renderOrder = 3;
    progressRing.frustumCulled = false;
    group.add(progressRing);
    progressRings.push(progressRing);
  });
  selectProgressRing();
  showShell();
  drawAtlas(meta);
  element('connection').textContent = `${meta.shell.ringCount} 级进度截面 · Shell 间隙 ${meta.shell.clearance * 1000} mm`;
  element('connection-dot').classList.add('ready');
}

function drawAtlas(meta) {
  for (let surface = 0; surface < meta.trajectorySurfaces; surface++) {
    const canvas = element(`atlas-${surface}`);
    canvas.width = meta.trajectoryColumns;
    canvas.height = meta.trajectoryRows;
    const context = canvas.getContext('2d');
    for (let row = 0; row < meta.trajectoryRows; row++) for (let column = 0; column < meta.trajectoryColumns; column++) {
      const weight = meta.trajectoryWeights[(surface * meta.trajectoryRows + row) * meta.trajectoryColumns + column];
      color.copy(attachedColor).lerp(simulatedColor, weight);
      context.fillStyle = `#${color.getHexString()}`;
      context.fillRect(column, meta.trajectoryRows - row - 1, 1, 1);
    }
  }
}

function updatePositions(mesh, values) {
  mesh.attributes.position.array.set(values);
  mesh.attributes.position.needsUpdate = true;
  mesh.computeVertexNormals();
  mesh.computeBoundingSphere();
}

function showFrame(values) {
  const advanced = !latestFrame || values[11] !== latestFrame[11];
  latestFrame = values;
  if (!advanced) return;
  let offset = description.headerLength;
  const bodyPositions = values.subarray(offset, offset += description.bodyVertices * 3);
  const bodyNormals = values.subarray(offset, offset += description.bodyVertices * 3);
  const drawPositions = bodyGeometry.attributes.position.array, drawNormals = bodyGeometry.attributes.normal.array;
  for (let vertex = 0; vertex < description.bodyVertexMap.length; vertex++) {
    const source = description.bodyVertexMap[vertex] * 3, target = vertex * 3;
    drawPositions[target] = bodyPositions[source]; drawPositions[target + 1] = bodyPositions[source + 1]; drawPositions[target + 2] = bodyPositions[source + 2];
    drawNormals[target] = bodyNormals[source]; drawNormals[target + 1] = bodyNormals[source + 1]; drawNormals[target + 2] = bodyNormals[source + 2];
  }
  bodyGeometry.attributes.position.needsUpdate = true;
  bodyGeometry.attributes.normal.needsUpdate = true;
  bodyGeometry.computeBoundingSphere();
  for (const head of heads) {
    head.quaternion.set(values[12], values[13], values[14], values[15]);
    head.position.set(values[16], values[17], values[18]);
  }
  for (let i = 0; i < garments.length; i++) {
    const count = i ? description.tights.vertices : description.briefs.vertices;
    updatePositions(garments[i].geometry, values.subarray(offset, offset += count * 3));
  }
  const weights = [];
  const stretch = [];
  for (const garment of [description.briefs, description.tights]) weights.push(values.subarray(offset, offset += garment.vertices));
  for (const garment of [description.briefs, description.tights]) stretch.push(values.subarray(offset, offset += garment.vertices));
  const trackPositions = values.subarray(offset, offset += description.trajectoryVertices * 3);
  trackGeometry.attributes.position.array.set(trackPositions);
  trackGeometry.attributes.position.needsUpdate = true;
  trackGeometry.computeBoundingSphere();
  updatePositions(shellGeometry, values.subarray(offset, offset + description.shell.vertices * 3));
  updateColors(weights, stretch);
  if (advanced) {
    timings.push([values[3], values[4], values[19]]);
    if (timings.length > 60) timings.shift();
  }
  if (values[11] - lastUiFrame >= 8 || lastUiFrame === 0) {
    lastUiFrame = values[11];
    const median = channel => timings.map(sample => sample[channel]).sort((a, b) => a - b)[Math.floor(timings.length / 2)];
    element('skin-time').textContent = `${median(0).toFixed(3)} ms`;
    element('cloth-time').textContent = `${median(1).toFixed(3)} ms`;
    element('surface-time').textContent = `${median(2).toFixed(3)} ms`;
    element('brief-stats').textContent = `模拟 ${values[5]} / ${description.briefs.particles} · 越界 ${values[7]}`;
    element('tights-stats').textContent = `模拟 ${values[6]} / ${description.tights.particles} · 越界 ${values[8]}`;
    element('brief-value').textContent = `${Math.round(values[1] * 100)}%`;
    element('tights-value').textContent = `${Math.round(values[2] * 100)}%`;
  }
}

function updateColors(weights, stretch) {
  redraw = true;
  garments.forEach((garment, index) => {
    const colors = garment.geometry.attributes.color;
    for (let vertex = 0; vertex < colors.count; vertex++) {
      if (colorMode === 'fabric') {
        color.copy(clothColors[index]);
        const trim = (index ? description.tights : description.briefs).trim[vertex];
        color.multiplyScalar(1 - trim * .28);
      } else {
        const amount = colorMode === 'weight' ? weights[index][vertex] : Math.min(1, (stretch[index][vertex] - 1) / .5);
        color.copy(attachedColor).lerp(simulatedColor, amount);
      }
      colors.setXYZ(vertex, color.r, color.g, color.b);
    }
    colors.needsUpdate = true;
  });
}

const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/motion`);
socket.binaryType = 'arraybuffer';
socket.addEventListener('message', message => {
  if (typeof message.data === 'string') build(JSON.parse(message.data));
  else { showFrame(new Float32Array(message.data)); readyToSend = true; }
});
socket.addEventListener('close', () => { element('connection').textContent = '模拟器已断开'; element('connection-dot').classList.remove('ready'); });

function setProgress(briefs, tights) {
  controls.briefs = briefs;
  controls.tights = tights;
  element('brief-progress').value = briefs * 100;
  element('tights-progress').value = tights * 100;
}
element('brief-progress').addEventListener('input', event => {
  const value = Number(event.target.value) / 100;
  setProgress(value, element('synchronize').checked ? value : controls.tights);
});
element('tights-progress').addEventListener('input', event => {
  const value = Number(event.target.value) / 100;
  setProgress(element('synchronize').checked ? value : controls.briefs, value);
});
element('undress').onclick = () => setProgress(1, 1);
element('dress').onclick = () => setProgress(0, 0);
element('reset').onclick = () => {
  setProgress(0, 0);
  if (controls.shellOnly) { element('running').checked = false; controls.running = 0; }
  controls.reset = true; timings = []; lastUiFrame = 0;
};
element('pause').onclick = () => { controls.paused = !controls.paused; element('pause').textContent = controls.paused ? '继续' : '暂停'; };
const updateRunning = () => controls.running = element('running').checked ? Number(element('stride').value) / 100 : 0;
element('running').onchange = updateRunning;
element('stride').oninput = event => { element('stride-value').textContent = `${event.target.value}%`; updateRunning(); };
element('spread').oninput = event => { controls.spread = Number(event.target.value) * Math.PI / 180; element('spread-value').textContent = `${event.target.value}°`; };
element('wind').oninput = event => { controls.wind = Number(event.target.value); element('wind-value').textContent = controls.wind.toFixed(1); };
element('skinning').onchange = event => controls.dualQuaternion = event.target.value === 'dq';
element('quality').onchange = event => controls.fine = event.target.value === 'fine';
function showShell() {
  document.body.classList.toggle('shell-view', controls.shellOnly);
  element('reset').textContent = controls.shellOnly ? '恢复站姿' : '重新穿好';
  shells.forEach(shell => shell.visible = controls.shellOnly);
  shellWires.forEach(wire => wire.visible = controls.shellOnly && element('show-shell-wire').checked);
  progressRings.forEach(ring => ring.visible = controls.shellOnly);
  garments.forEach(garment => garment.visible = !controls.shellOnly);
  seams.forEach(seam => seam.visible = !controls.shellOnly);
  wires.forEach(wire => wire.visible = !controls.shellOnly && element('show-wire').checked);
  tracks.forEach(track => track.visible = !controls.shellOnly && element('show-track').checked);
  element('garment-labels').style.display = controls.shellOnly ? 'none' : '';
  if (description) element('connection').textContent = controls.shellOnly
    ? `${description.shell.ringCount} 级进度截面 · Shell 间隙 ${description.shell.clearance * 1000} mm`
    : `${description.briefs.vertices + description.tights.vertices} 个衣物顶点 · C# 模拟器`;
  redraw = true;
}
function selectProgressRing() {
  const progress = Number(element('shell-progress').value) / 100;
  element('shell-progress-value').textContent = `${(progress * 100).toFixed(1)}%`;
  if (!description) return;
  const values = description.shell.ringProgress;
  const row = values.reduce((best, value, index) => Math.abs(value - progress) < Math.abs(values[best] - progress) ? index : best, 0);
  const edges = [];
  for (let side = 0; side < 2; side++) {
    const ring = description.shell.rings[side * description.shell.ringCount + row];
    for (let column = 0; column + 1 < ring.length; column++) edges.push(ring[column], ring[column + 1]);
  }
  progressGeometry.setIndex(edges);
  redraw = true;
}
element('shell-progress').oninput = selectProgressRing;
element('shell-only').onchange = event => { controls.shellOnly = event.target.checked; showShell(); };
element('show-shell-wire').onchange = showShell;
element('show-track').onchange = showShell;
element('show-wire').onchange = showShell;
element('see-through').onchange = event => bodies.forEach(body => {
  redraw = true;
  for (const material of body.material) {
    material.transparent = event.target.checked;
    material.opacity = event.target.checked ? .22 : 1;
    material.depthWrite = !event.target.checked;
    material.needsUpdate = true;
  }
  body.castShadow = !event.target.checked;
});
document.querySelectorAll('[data-color]').forEach(button => button.onclick = () => {
  colorMode = button.dataset.color;
  document.querySelectorAll('[data-color]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  element('color-legend').hidden = colorMode === 'fabric';
  element('legend-left').textContent = colorMode === 'weight' ? '蒙皮跟随' : '参考长度';
  element('legend-right').textContent = colorMode === 'weight' ? '完全模拟' : '拉长 50%';
  if (latestFrame) {
    let offset = description.headerLength + description.bodyVertices * 6 + (description.briefs.vertices + description.tights.vertices) * 3;
    const weights = [description.briefs, description.tights].map(garment => latestFrame.subarray(offset, offset += garment.vertices));
    const stretch = [description.briefs, description.tights].map(garment => latestFrame.subarray(offset, offset += garment.vertices));
    updateColors(weights, stretch);
  }
});
document.querySelectorAll('button[data-garment]').forEach(button => button.onclick = () => {
  garmentView = button.dataset.garment;
  figures.forEach((figure, index) => {
    figure.visible = garmentView === 'both' || index === (garmentView === 'briefs' ? 0 : 1);
    figure.position.x = garmentView === 'both' ? (index ? .58 : -.58) : 0;
  });
  element('garment-labels').dataset.garment = garmentView;
  document.querySelectorAll('button[data-garment]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  fitCamera();
});
document.querySelectorAll('[data-view]').forEach(button => button.onclick = () => {
  cameraView = button.dataset.view;
  fitCamera();
});
function fitCamera() {
  redraw = true;
  camera = cameraView === 'feet-side' ? sideCamera : perspectiveCamera;
  orbit.object = camera;
  if (camera === sideCamera) {
    sideCamera.left = -.18 * perspectiveCamera.aspect;
    sideCamera.right = .18 * perspectiveCamera.aspect;
    sideCamera.zoom = 1;
    sideCamera.updateProjectionMatrix();
    orbit.target.set(.15, .14, .10);
    sideCamera.position.set(2, .14, .10);
    return;
  }
  const close = cameraView === 'crotch', feet = cameraView === 'feet' || cameraView === 'feet-side';
  const halfHeight = feet ? .16 : close ? .38 : 1.02;
  const halfWidth = garmentView === 'both' ? 1.02 : feet ? .18 : close ? .34 : .44;
  const distance = Math.max(halfHeight, halfWidth / camera.aspect) / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  const direction = cameraView === 'back' ? -1 : 1;
  if (feet) {
    orbit.target.set(.15, .10, .12);
    camera.position.set(.15 + distance * .8, .10 + distance * .35, .12 + distance * .6);
    return;
  }
  orbit.target.set(0, feet ? .17 : close ? .77 : .85, 0);
  camera.position.set(.08 * direction, feet ? .30 : close ? .80 : 1.0, distance * direction);
}
new ResizeObserver(() => {
  const width = viewport.clientWidth, height = viewport.clientHeight;
  perspectiveCamera.aspect = width / height;
  perspectiveCamera.updateProjectionMatrix();
  fitCamera();
  renderer.setSize(width, height);
}).observe(viewport);

function animate(time) {
  requestAnimationFrame(animate);
  elapsedSeconds = Math.min(.1, elapsedSeconds + (time - previousFrameTime) / 1000);
  previousFrameTime = time;
  if (controls.paused) elapsedSeconds = 0;
  if (orbit.update()) redraw = true;
  if (redraw) {
    figures.forEach((figure, index) => {
      labelPosition.set(figure.position.x, 0, 0).project(camera);
      garmentLabels[index].style.left = `${(labelPosition.x * .5 + .5) * viewport.clientWidth}px`;
    });
    renderer.render(scene, camera);
    redraw = false;
  }
  if (readyToSend && socket.readyState === WebSocket.OPEN && (elapsedSeconds >= 1 / 60 || controls.reset)) {
    readyToSend = false;
    const frameSteps = Math.max(1, Math.min(4, Math.floor(elapsedSeconds * 60)));
    elapsedSeconds = Math.max(0, elapsedSeconds - frameSteps / 60);
    socket.send(JSON.stringify({ ...controls, frameSteps }));
    controls.reset = false;
  }
}
requestAnimationFrame(animate);
