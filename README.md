# MirrorPositionSync

Network Transform using Snapshot Interpolation and other techniques to best sync position and rotation over the network. 

## Usage

1) Download the code from the source folder or package on [Release](https://github.com/JamesFrowen/MirrorPositionSync/releases) page.
2) Put the code somewhere in your Assets folder
3) Add Component to GameObjects that you want to sync

## Bugs?

Please report any bugs or issues [Here](https://github.com/JamesFrowen/MirrorPositionSync/issues)


# Goals

In order of priority
- Easy to use 
- Smoothly sync movement 
- Low bandwidth
- Low latency
- Low Cpu usage


### Easy to use
- The components should be easy to setup.
- Behaviour as close to normal transform as possible
    - position
    - rotation
    - parent/child
    - *not scale*
        - scale does not change as often as position/rotation and does effect position like position does
- Avoid pointless fields in inspector
    - advance config could be in dropdown
- Include debug tools and gizmos


### Smoothly sync movement 
- look smooth at different sync intervals
- use LERP with snapshot buffer
    - make sure there is always a target snapshot


### Low bandwidth
- dont send data unless it is needed 
    - dont send position/rotation if unchanged
    - have option to only send some data (eg 2d game: only send xy pos, and z rotation) (this is future work, might become separate component)
- use bitpacking
- group data into single message
- send over UDP
- delta snapshots
    - kept track of send snapshots and which ones clients have received 
    - send data compared to last received snapshot
    - This allows variable size bitpacking to keep number of bits low


### Low latency
- use techniques to measure jitter and lower client delay
- send over UDP


### Low Cpu usage
- code should perform fast
- code should be easy to understand
    - anything that is harder to understand for performance should be in its own function/class so they the general flow of the code is easy to understand
- benchmark and test different bits of code to find out which performance better
- Server > client
    - server: cpu usage is important 
    - client: should be good enough to not cause problems