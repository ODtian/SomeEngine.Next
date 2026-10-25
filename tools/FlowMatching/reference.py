"""NumPy reference for the exported ELU model and the row-vector pose decoder.

This evaluator imports neither the training code nor the engine. Its math follows
ControlOperators controller.py:924-964; the declared model selects ELU MLPs.
"""
import argparse
import json
from pathlib import Path
import numpy as np


def network(value, definition):
    for index, (source, target) in enumerate(zip(definition["widths"], definition["widths"][1:])):
        weight = np.asarray(definition["weights"][index], np.float32).reshape(target, source)
        value = value @ weight.T + np.asarray(definition["biases"][index], np.float32)
        if index < len(definition["weights"]) - 1:
            value = np.where(value >= 0, value, np.expm1(np.minimum(value, 0)))
    return value


def integrate(model, history, control, noise):
    deviation = np.asarray(model["latentDeviation"], np.float32)
    latent = noise * deviation
    encoded = (control - model["controlOffset"]) / model["controlScale"]
    for step in range(model["steps"]):
        inputs = np.concatenate((latent, history, encoded.astype(np.float32),
                                 np.array([step / model["steps"]], np.float32)))
        latent = latent + np.float32(1 / model["steps"]) * deviation * network(inputs, model["flow"])
    return np.clip(latent, model["latentMinimum"], model["latentMaximum"]).astype(np.float32)


def multiply(a, b):
    return np.concatenate((a[3] * b[:3] + b[3] * a[:3] + np.cross(a[:3], b[:3]),
                           np.array([a[3] * b[3] - a[:3] @ b[:3]], np.float32)))


def rotate(rotation, value):
    return value + 2 * np.cross(rotation[:3], np.cross(rotation[:3], value) + rotation[3] * value)


def rotation_matrix(rotation):
    axes = np.stack([rotate(rotation, axis) for axis in np.eye(3, dtype=np.float32)])
    result = np.eye(4, dtype=np.float32)
    result[:3, :3] = axes
    return result


def decode(model, latent, position, rotation, dt):
    decoder_input = latent * np.asarray(model["latentScale"], np.float32) + model["latentOffset"]
    pose = network(decoder_input.astype(np.float32), model["decoder"])
    pose = (pose * np.asarray(model["poseDeviation"], np.float32)
            * np.asarray(model["poseScale"], np.float32) + model["poseOffset"]).astype(np.float32)
    position = position + dt * rotate(rotation, pose[:3])
    angular = dt * rotate(rotation, pose[3:6])
    angle = np.linalg.norm(angular)
    delta = np.concatenate((angular * (np.sin(angle / 2) / angle if angle > 0 else .5),
                            np.array([np.cos(angle / 2)], np.float32)))
    rotation = multiply(delta, rotation)
    root = rotation_matrix(rotation)
    root[3, :3] = position
    local = [root]
    for bone in range(model["boneCount"] - 1):
        axes = pose[9 + 6 * bone:15 + 6 * bone].reshape(3, 2)
        x = axes[:, 0] / np.linalg.norm(axes[:, 0])
        y = axes[:, 1] - x * (x @ axes[:, 1])
        y /= np.linalg.norm(y)
        matrix = np.eye(4, dtype=np.float32)
        matrix[:3, :3] = np.stack((x, y, np.cross(x, y)))
        matrix[3, :3] = pose[6:9] if bone == 0 else np.array([0, 1, 0], np.float32)
        local.append(matrix)
    model_pose = [local[0], local[1] @ local[0], local[2] @ local[1] @ local[0]]
    return pose, position, rotation, local, model_pose


def example(model):
    controls = np.array([[.7, .1, 0, .9, .2, 0], [-.65, -.2, 0, -.8, -.5, 0]], np.float32)
    noise = np.array([[.2, -.3, .1, .4, -.2, .3], [-.1, .4, -.3, .1, .2, -.4],
                      [.3, .1, -.2, -.1, .4, .2]], np.float32)
    frames = []
    histories = [np.asarray(model["initialHistory"], np.float32).copy() for _ in range(2)]
    positions = [np.zeros(3, np.float32) for _ in range(2)]
    rotations = [np.array([0, 0, 0, 1], np.float32) for _ in range(2)]
    for frame in range(3):
        characters = []
        for actor in range(2):
            histories[actor] = integrate(model, histories[actor], controls[actor], noise[frame])
            pose, positions[actor], rotations[actor], local, matrices = decode(
                model, histories[actor], positions[actor], rotations[actor], model["frameTime"])
            characters.append({"history": histories[actor].tolist(), "pose": pose.tolist(),
                               "local": [matrix.reshape(-1).tolist() for matrix in local],
                               "model": [matrix.reshape(-1).tolist() for matrix in matrices]})
        frames.append(characters)
    return {"controls": controls.tolist(), "noise": noise.tolist(), "frames": frames}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    model = json.loads(args.model.read_text(encoding="utf-8"))
    result = example(model)
    args.output.write_text(json.dumps(result), encoding="utf-8")
    for actor, values in enumerate(result["frames"][-1]):
        print(f"actor={actor} root_velocity={values['pose'][:3]} hip={values['pose'][6:9]} history={values['history']}", flush=True)
