"""Compare the training networks with an independently read version-1 export.

Inputs use the complete declared control and latent dimensions.
New training checks its in-memory networks; --model reuses an existing learned asset.
"""
import argparse
import json
from pathlib import Path
import numpy as np
import torch
from reference import integrate, network as reference_network


def check_export(model, flow, decoder, tolerance=.0002):
    exported = json.loads(json.dumps(model))
    random = np.random.default_rng(4862)
    control_values = (np.asarray(model["controlOffset"], np.float32) + np.asarray(model["controlScale"], np.float32)
                      * random.uniform(-1, 1, (2, len(model["controlOffset"])))).astype(np.float32)
    noise_values = random.normal(size=(3, 2, len(model["initialHistory"]))).astype(np.float32)
    expected_history = np.tile(np.asarray(model["initialHistory"], np.float32), (2, 1))
    histories = torch.tensor(model["initialHistory"], dtype=torch.float32).repeat(2, 1)
    deviation = torch.tensor(model["latentDeviation"], dtype=torch.float32)
    controls = ((torch.tensor(control_values) - torch.tensor(model["controlOffset"]))
                / torch.tensor(model["controlScale"]))
    maximum = 0.
    with torch.no_grad():
        for frame in range(3):
            latent = torch.tensor(noise_values[frame]) * deviation
            for step in range(model["steps"]):
                time = torch.full((2, 1), step / model["steps"])
                latent += flow(torch.cat((latent, histories, controls, time), dim=1)) * deviation / model["steps"]
            histories = torch.clamp(latent, min=torch.tensor(model["latentMinimum"]),
                                     max=torch.tensor(model["latentMaximum"]))
            decoded = decoder(histories * torch.tensor(model["latentScale"]) + torch.tensor(model["latentOffset"]))
            decoded = (decoded * torch.tensor(model["poseDeviation"]) * torch.tensor(model["poseScale"])
                       + torch.tensor(model["poseOffset"]))
            expected_history = np.stack([integrate(exported, expected_history[actor], control_values[actor],
                                                   noise_values[frame, actor]) for actor in range(2)])
            decoder_input = expected_history * np.asarray(exported["latentScale"], np.float32) + exported["latentOffset"]
            expected_pose = reference_network(decoder_input.astype(np.float32), exported["decoder"])
            expected_pose = (expected_pose * np.asarray(exported["poseDeviation"], np.float32)
                             * np.asarray(exported["poseScale"], np.float32) + exported["poseOffset"])
            history_error = np.abs(histories.numpy() - expected_history).max()
            pose_error = np.abs(decoded.numpy() - expected_pose).max()
            maximum = max(maximum, float(history_error), float(pose_error))
    print(f"training/export maximum error={maximum:.9g} tolerance={tolerance}", flush=True)
    if maximum > tolerance:
        raise ValueError("Training and exported model results exceed the declared tolerance.")


if __name__ == "__main__":
    from train import mlp
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    args = parser.parse_args()
    torch.set_num_threads(4)
    model = json.loads(args.model.read_text(encoding="utf-8"))
    networks = []
    for name in ("flow", "decoder"):
        definition = model[name]
        network = mlp(definition["widths"])
        with torch.no_grad():
            for index, layer in enumerate(layer for layer in network if isinstance(layer, torch.nn.Linear)):
                layer.weight.copy_(torch.tensor(definition["weights"][index]).reshape_as(layer.weight))
                layer.bias.copy_(torch.tensor(definition["biases"][index]))
        networks.append(network)
    check_export(model, *networks)
