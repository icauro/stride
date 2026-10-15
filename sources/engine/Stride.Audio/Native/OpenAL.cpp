// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#include "Common.h"

#if defined(PLATFORM_LINUX) || defined(PLATFORM_MACOS) || defined(IOS) || !defined(__clang__)

#include "../../../deps/NativePath/NativePath.h"
#include "../../../deps/NativePath/NativeDynamicLinking.h"
#include "../../../deps/NativePath/NativeMemory.h"
#include "../../../deps/NativePath/NativeThreading.h"
#include "../../../deps/NativePath/TINYSTL/unordered_set.h"
#include "../../../deps/NativePath/TINYSTL/unordered_map.h"
#include "../../../deps/NativePath/TINYSTL/vector.h"
#include "../../Stride.Native/StrideNative.h"


#define HAVE_STDINT_H
#include "../../../../deps/OpenAL/AL/al.h"
#include "../../../../deps/OpenAL/AL/alc.h"
#include "../../../../deps/OpenAL/AL/alext.h"

extern "C" {
	class SpinLock
	{
	public:
		SpinLock()
		{
			mLocked = false;
		}

		void Lock()
		{			
			while(!__sync_bool_compare_and_swap(&mLocked, false, true)) {}
		}

		void Unlock()
		{
			mLocked = false;
		}

	private:
		volatile bool mLocked;
	};

	namespace OpenAL
	{
		LPALCOPENDEVICE OpenDevice;
		LPALCCLOSEDEVICE CloseDevice;
		LPALCCREATECONTEXT CreateContext;
		LPALCDESTROYCONTEXT DestroyContext;
		LPALCMAKECONTEXTCURRENT MakeContextCurrent;
		LPALCGETCURRENTCONTEXT GetCurrentContext;
		LPALCPROCESSCONTEXT ProcessContext;
		LPALCGETERROR GetErrorALC;
        LPALCISEXTENSIONPRESENT IsDeviceExtensionPresent;
        LPALCGETINTEGERV GetDeviceIntegerv;
		LPALCSUSPENDCONTEXT SuspendContext;
		
		LPALSOURCEPLAY SourcePlay;
		LPALSOURCEPAUSE SourcePause;
		LPALSOURCESTOP SourceStop;
		LPALSOURCEF SourceF;
		LPALDELETESOURCES DeleteSources;
		LPALDELETEBUFFERS DeleteBuffers;
		LPALGENSOURCES GenSources;
		LPALGENBUFFERS GenBuffers;
		LPALSOURCE3I Source3I;
		LPALSOURCEI SourceI;
		LPALBUFFERDATA BufferData;
		LPALSOURCEQUEUEBUFFERS SourceQueueBuffers;
		LPALSOURCEUNQUEUEBUFFERS SourceUnqueueBuffers;
		LPALGETSOURCEI GetSourceI;
		LPALGETSOURCEF GetSourceF;
		LPALSOURCEFV SourceFV;
		LPALLISTENERFV ListenerFV;
		LPALLISTENERF ListenerF;
		LPALGETERROR GetErrorAL;

		void* OpenALLibrary = NULL;

		class ContextState
		{
		public:
			ContextState(ALCcontext* context, bool keepContext = false)
			{
				sOpenAlLock.Lock();

				mKeepContext = keepContext;
                mOldContext = GetCurrentContext();
				if (context != mOldContext)
				{
					MakeContextCurrent(context);
					swap = true;
				}
				else
				{
					swap = false;
				}
			}

			~ContextState()
			{
				if (swap && !mKeepContext)
				{
					MakeContextCurrent(mOldContext);
				}
				
				sOpenAlLock.Unlock();
			}

		private:
			bool swap;
            bool mKeepContext;
			ALCcontext* mOldContext;
			static SpinLock sOpenAlLock;
		};

		SpinLock ContextState::sOpenAlLock;

		DLL_EXPORT_API npBool xnAudioInit()
		{
			if (OpenALLibrary) return true;

			//Generic
			OpenALLibrary = LoadDynamicLibrary("OpenAL");
			OpenALLibrary = LoadDynamicLibrary("openal");
			
			//PC - Windows
			if(sizeof(intptr_t) == 4)
			{
				if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("x86\\OpenAL");
				if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("x86/OpenAL");
			}
			else
			{
				if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("x64\\OpenAL");
				if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("x64/OpenAL");
			}
			
			//iOS
			if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("/System/Library/Frameworks/OpenAL.framework/OpenAL"); //iOS Apple OpenAL

			//Linux
			if (!OpenALLibrary) OpenALLibrary = LoadDynamicLibrary("libopenal.so.1");
			
			if (!OpenALLibrary) return false;

			OpenDevice = (LPALCOPENDEVICE)GetSymbolAddress(OpenALLibrary, "alcOpenDevice");
			if (!OpenDevice) return false;
			CloseDevice = (LPALCCLOSEDEVICE)GetSymbolAddress(OpenALLibrary, "alcCloseDevice");
			if (!CloseDevice) return false;
			CreateContext = (LPALCCREATECONTEXT)GetSymbolAddress(OpenALLibrary, "alcCreateContext");
			if (!CreateContext) return false;
			DestroyContext = (LPALCDESTROYCONTEXT)GetSymbolAddress(OpenALLibrary, "alcDestroyContext");
			if (!DestroyContext) return false;
			MakeContextCurrent = (LPALCMAKECONTEXTCURRENT)GetSymbolAddress(OpenALLibrary, "alcMakeContextCurrent");
			if (!MakeContextCurrent) return false;
			GetCurrentContext = (LPALCGETCURRENTCONTEXT)GetSymbolAddress(OpenALLibrary, "alcGetCurrentContext");
			if (!GetCurrentContext) return false;
			ProcessContext = (LPALCPROCESSCONTEXT)GetSymbolAddress(OpenALLibrary, "alcProcessContext");
			if (!ProcessContext) return false;
			GetErrorALC = (LPALCGETERROR)GetSymbolAddress(OpenALLibrary, "alcGetError");
			if (!GetErrorALC) return false;
            IsDeviceExtensionPresent = (LPALCISEXTENSIONPRESENT)GetSymbolAddress(OpenALLibrary, "alcIsExtensionPresent");
            GetDeviceIntegerv = (LPALCGETINTEGERV)GetSymbolAddress(OpenALLibrary, "alcGetIntegerv");
			SuspendContext = (LPALCSUSPENDCONTEXT)GetSymbolAddress(OpenALLibrary, "alcSuspendContext");
			if (!SuspendContext) return false;

			SourcePlay = (LPALSOURCEPLAY)GetSymbolAddress(OpenALLibrary, "alSourcePlay");
			if (!SourcePlay) return false;
			SourcePause = (LPALSOURCEPAUSE)GetSymbolAddress(OpenALLibrary, "alSourcePause");
			if (!SourcePause) return false;
			SourceStop = (LPALSOURCESTOP)GetSymbolAddress(OpenALLibrary, "alSourceStop");
			if (!SourceStop) return false;
			SourceF = (LPALSOURCEF)GetSymbolAddress(OpenALLibrary, "alSourcef");
			if (!SourceF) return false;
			DeleteSources = (LPALDELETESOURCES)GetSymbolAddress(OpenALLibrary, "alDeleteSources");
			if (!DeleteSources) return false;
			DeleteBuffers = (LPALDELETEBUFFERS)GetSymbolAddress(OpenALLibrary, "alDeleteBuffers");
			if (!DeleteBuffers) return false;
			GenSources = (LPALGENSOURCES)GetSymbolAddress(OpenALLibrary, "alGenSources");
			if (!GenSources) return false;
			GenBuffers = (LPALGENBUFFERS)GetSymbolAddress(OpenALLibrary, "alGenBuffers");
			if (!GenBuffers) return false;
			Source3I = (LPALSOURCE3I)GetSymbolAddress(OpenALLibrary, "alSource3i");
			if (!Source3I) return false;
			SourceI = (LPALSOURCEI)GetSymbolAddress(OpenALLibrary, "alSourcei"); 
			if (!SourceI) return false;
			BufferData = (LPALBUFFERDATA)GetSymbolAddress(OpenALLibrary, "alBufferData");
			if (!BufferData) return false;
			SourceQueueBuffers = (LPALSOURCEQUEUEBUFFERS)GetSymbolAddress(OpenALLibrary, "alSourceQueueBuffers"); 
			if (!SourceQueueBuffers) return false;
			SourceUnqueueBuffers = (LPALSOURCEUNQUEUEBUFFERS)GetSymbolAddress(OpenALLibrary, "alSourceUnqueueBuffers");
			if (!SourceUnqueueBuffers) return false;
			GetSourceI = (LPALGETSOURCEI)GetSymbolAddress(OpenALLibrary, "alGetSourcei");
			if (!GetSourceI) return false;
			GetSourceF = (LPALGETSOURCEF)GetSymbolAddress(OpenALLibrary, "alGetSourcef");
			if (!GetSourceF) return false;
			SourceFV = (LPALSOURCEFV)GetSymbolAddress(OpenALLibrary, "alSourcefv");
			if (!SourceFV) return false;
			ListenerFV = (LPALLISTENERFV)GetSymbolAddress(OpenALLibrary, "alListenerfv");
			if (!ListenerFV) return false;
			ListenerF = (LPALLISTENERF)GetSymbolAddress(OpenALLibrary, "alListenerf");
			if (!ListenerF) return false;
			GetErrorAL = (LPALGETERROR)GetSymbolAddress(OpenALLibrary, "alGetError");
			if (!GetErrorAL) return false;

			return true;
		}

		#define AL_ERROR //if (auto err = GetErrorAL() != AL_NO_ERROR) debugtrap()
		#define ALC_ERROR(__device__) //if (auto err = GetErrorALC(__device__) != ALC_NO_ERROR) debugtrap()

		struct xnAudioListener;

		struct xnAudioDevice
		{
			ALCdevice* device;
			SpinLock deviceLock;
			tinystl::unordered_set<xnAudioListener*> listeners;
		};

		struct xnAudioBuffer
		{
			short* pcm = NULL;
			int size = 0;
            int capacityBytes = 0;
            ALCcontext* context = NULL;
            xnAudioListener* ownerListener = NULL;
            xnAudioBuffer* nextQueued = NULL;
			int sampleRate = 0;
			ALuint buffer;
			BufferType type = None;
		};

		struct xnAudioSource;

		struct xnAudioListener
		{
			xnAudioDevice* device;
			ALCcontext* context;
			tinystl::unordered_set<xnAudioSource*> sources;
			tinystl::unordered_map<ALuint, xnAudioBuffer*> buffers;
		};

		struct xnAudioSource
		{
			ALuint source;
			int sampleRate;
			bool mono;
			bool streamed;
            double rangeStart = 0;

			volatile double dequeuedTime = 0.0;

			xnAudioListener* listener;

			xnAudioBuffer* singleBuffer;

			tinystl::vector<xnAudioBuffer*> freeBuffers;
            xnAudioBuffer* queuedHead = NULL;
            xnAudioBuffer* queuedTail = NULL;
            void AppendQueued(xnAudioBuffer* buffer)
            {
                buffer->nextQueued = NULL;
                if (queuedTail) queuedTail->nextQueued = buffer; else queuedHead = buffer;
                queuedTail = buffer;
            }
            void RemoveQueued(xnAudioBuffer* buffer)
            {
                xnAudioBuffer* previous = NULL;
                for (auto current = queuedHead; current; current = current->nextQueued)
                {
                    if (current != buffer) { previous = current; continue; }
                    if (previous) previous->nextQueued = current->nextQueued; else queuedHead = current->nextQueued;
                    if (queuedTail == current) queuedTail = previous;
                    current->nextQueued = NULL; return;
                }
            }
		};

		DLL_EXPORT_API xnAudioDevice* xnAudioCreate(const char* deviceName, int flags)
		{
			auto res = new xnAudioDevice;
			res->device = OpenDevice(deviceName);
			ALC_ERROR(res->device);
			if (!res->device)
			{
				delete res;
				return NULL;
			}
			return res;
		}

		DLL_EXPORT_API void xnAudioDestroy(xnAudioDevice* device)
		{
			CloseDevice(device->device);
			ALC_ERROR(device->device);
			delete device;
		}

        DLL_EXPORT_API int xnAudioDeviceIsInvalidated(xnAudioDevice* device)
        {
            if (!device || !device->device) return 1;
            if (!IsDeviceExtensionPresent || !GetDeviceIntegerv || !IsDeviceExtensionPresent(device->device, "ALC_EXT_disconnect")) return -1;
            ALCint connected = ALC_TRUE;
            GetDeviceIntegerv(device->device, ALC_CONNECTED, 1, &connected);
            return connected == ALC_FALSE ? 1 : 0;
        }

		DLL_EXPORT_API void xnAudioUpdate(xnAudioDevice* device)
		{
			device->deviceLock.Lock();

			for (auto listener : device->listeners)
			{
				ContextState lock(listener->context);

				for(auto source : listener->sources)
				{
					if (source->streamed)
					{
						auto processed = 0;
						GetSourceI(source->source, AL_BUFFERS_PROCESSED, &processed);
						while (processed--)
						{
							ALfloat preDTime;
							GetSourceF(source->source, AL_SEC_OFFSET, &preDTime);

							ALuint buffer;
							SourceUnqueueBuffers(source->source, 1, &buffer);
							xnAudioBuffer* bufferPtr = source->listener->buffers[buffer];
                            source->RemoveQueued(bufferPtr);

							ALfloat postDTime;
							GetSourceF(source->source, AL_SEC_OFFSET, &postDTime);

							if (bufferPtr->type == EndOfStream || bufferPtr->type == EndOfLoop)
							{
								source->dequeuedTime = 0.0;
							}
							else
							{
								source->dequeuedTime += preDTime - postDTime;
							}

							source->freeBuffers.push_back(bufferPtr);
						}
					}
				}
			}
			
			device->deviceLock.Unlock();
		}

		DLL_EXPORT_API xnAudioListener* xnAudioListenerCreate(xnAudioDevice* device)
		{
			auto res = new xnAudioListener;
			res->device = device;

			res->context = CreateContext(device->device, NULL);
			ALC_ERROR(device->device);
			MakeContextCurrent(res->context);
			ALC_ERROR(device->device);
			ProcessContext(res->context);
			ALC_ERROR(device->device);

			device->deviceLock.Lock();

			device->listeners.insert(res);

			device->deviceLock.Unlock();

			return res;
		}

		DLL_EXPORT_API void xnAudioListenerDestroy(xnAudioListener* listener)
		{
			listener->device->deviceLock.Lock();

			listener->device->listeners.erase(listener);

			listener->device->deviceLock.Unlock();

			// Clear the current context so Apple's OpenAL stops rendering it before we free it;
			// otherwise its audio callback can race the freed source list and abort on macOS shutdown.
			MakeContextCurrent(NULL);

			DestroyContext(listener->context);

			delete listener;
		}

		DLL_EXPORT_API void xnAudioSetMasterVolume(xnAudioDevice* device, float volume)
		{
			device->deviceLock.Lock();
			for(auto listener : device->listeners)
			{
				ContextState lock(listener->context);
				ListenerF(AL_GAIN, volume);
			}
			device->deviceLock.Unlock();
		}

		DLL_EXPORT_API npBool xnAudioListenerEnable(xnAudioListener* listener)
		{
			ContextState lock(listener->context, true);
            bool res = GetCurrentContext() == listener->context;
			ProcessContext(listener->context);
			return res;
		}

		DLL_EXPORT_API void xnAudioListenerDisable(xnAudioListener* listener)
		{
			ContextState lock(listener->context, true);
            SuspendContext(listener->context);
			MakeContextCurrent(NULL);
		}

		DLL_EXPORT_API xnAudioSource* xnAudioSourceCreate(xnAudioListener* listener, int sampleRate, int maxNBuffers, npBool mono, npBool spatialized, npBool streamed, npBool hrtf, float directionFactor, int environment)
		{
			(void)spatialized;
			(void)maxNBuffers;

			auto res = new xnAudioSource;
			res->listener = listener;
			res->sampleRate = sampleRate;
			res->mono = mono;
			res->streamed = streamed;

			ContextState lock(listener->context);

			GenSources(1, &res->source);
			AL_ERROR;
			SourceF(res->source, AL_REFERENCE_DISTANCE, 1.0f);
			AL_ERROR;

			if(spatialized)
			{
				//make sure we are able to 3D
				SourceI(res->source, AL_SOURCE_RELATIVE, AL_FALSE);
			}
			else
			{
				//make sure we are able to pan
				SourceI(res->source, AL_SOURCE_RELATIVE, AL_TRUE);
			}

			listener->sources.insert(res);
			
			return res;
		}

		DLL_EXPORT_API void xnAudioSourceDestroy(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			DeleteSources(1, &source->source);
			AL_ERROR;

			source->listener->sources.erase(source);

			delete source;
		}

		DLL_EXPORT_API double xnAudioSourceGetPosition(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			ALfloat offset;
			GetSourceF(source->source, AL_SEC_OFFSET, &offset);

			if (!source->streamed)
			{				
				return offset + source->rangeStart;
			}

			return offset + source->dequeuedTime;
		}

		DLL_EXPORT_API void xnAudioSourceSetPan(xnAudioSource* source, float pan)
		{
			auto clampedPan = pan > 1.0f ? 1.0f : pan < -1.0f ? -1.0f : pan;
			ALfloat alpan[3];
			alpan[0] = clampedPan; // from -1 (left) to +1 (right) 
			alpan[1] = sqrt(1.0f - clampedPan*clampedPan);
			alpan[2] = 0.0f;

			ContextState lock(source->listener->context);

			SourceFV(source->source, AL_POSITION, alpan);
		}

		DLL_EXPORT_API void xnAudioSourceSetLooping(xnAudioSource* source, npBool looping)
		{
			ContextState lock(source->listener->context);

			SourceI(source->source, AL_LOOPING, looping ? AL_TRUE : AL_FALSE);
		}

        DLL_EXPORT_API int xnAudioRecoveryCapabilities() { return 1 | 2; }
        DLL_EXPORT_API int xnAudioSourceGetPlaybackState(xnAudioSource* source)
        {
            if (!source) return -1;
            ContextState lock(source->listener->context);
            (void)GetErrorAL();
            ALint state; GetSourceI(source->source, AL_SOURCE_STATE, &state);
            if (GetErrorAL() != AL_NO_ERROR) return -1;
            return state == AL_PLAYING ? 1 : state == AL_PAUSED ? 2 : 0;
        }
        DLL_EXPORT_API npBool xnAudioPrepareStreamRecovery(xnAudioSource* original, xnAudioSource* replacement,
            xnAudioBuffer** oldPool, xnAudioBuffer** newPool, int count, xnAudioBuffer** managedFree, int freeCount)
        {
            if (!original || !replacement || !original->streamed || !replacement->streamed
                || count <= 0 || freeCount < 0 || freeCount > count) return false;
            tinystl::vector<xnAudioBuffer*> pending;
            tinystl::vector<xnAudioBuffer*> nativeFree;
            ALint firstOffset = 0;
            double dequeued = original->dequeuedTime;
            {
                ContextState lock(original->listener->context);
                (void)GetErrorAL();
                ALint state, queuedCount, processed, offset;
                GetSourceI(original->source, AL_SOURCE_STATE, &state);
                GetSourceI(original->source, AL_BUFFERS_QUEUED, &queuedCount);
                GetSourceI(original->source, AL_BUFFERS_PROCESSED, &processed);
                GetSourceI(original->source, AL_SAMPLE_OFFSET, &offset);
                if (GetErrorAL() != AL_NO_ERROR || state == AL_PLAYING || processed < 0 || processed > queuedCount) return false;
                for (int i = 0; i < count; ++i)
                {
                    if (!oldPool[i] || !newPool[i] || oldPool[i]->size < 0
                        || oldPool[i]->size > oldPool[i]->capacityBytes || oldPool[i]->size > newPool[i]->capacityBytes) return false;
                    for (int j = 0; j < i; ++j)
                        if (oldPool[i] == oldPool[j] || newPool[i] == newPool[j]) return false;
                }
                for (int i = 0; i < freeCount; ++i)
                {
                    bool found = false;
                    for (int j = 0; j < count; ++j) if (managedFree[i] == oldPool[j]) found = true;
                    for (int j = 0; j < i; ++j) if (managedFree[i] == managedFree[j]) found = false;
                    if (!found) return false;
                }
                int observed = 0;
                for (auto buffer = original->queuedHead; buffer; buffer = buffer->nextQueued)
                {
                    if (++observed > count) return false;
                    int index = 0; while (index < count && oldPool[index] != buffer) ++index;
                    if (index == count) return false;
                    for (int i = 0; i < freeCount; ++i) if (managedFree[i] == buffer) return false;
                    const auto frames = buffer->size / (sizeof(short) * (original->mono ? 1 : 2));
                    if (observed <= processed)
                    {
                        nativeFree.push_back(newPool[index]);
                        if (state != AL_STOPPED)
                        {
                            offset -= frames;
                            if (buffer->type == EndOfStream || buffer->type == EndOfLoop) dequeued = 0;
                            else dequeued += double(frames) / original->sampleRate;
                        }
                        else if (buffer->type == EndOfStream || buffer->type == EndOfLoop) dequeued = 0;
                    }
                    else pending.push_back(newPool[index]);
                }
                if (observed != queuedCount || offset < 0) return false;
                firstOffset = offset;
                if (pending.size() == 0) firstOffset = 0;
                else
                {
                    int index = 0; while (newPool[index] != pending.front()) ++index;
                    if (firstOffset >= oldPool[index]->size / (int)(sizeof(short) * (original->mono ? 1 : 2))) return false;
                }
                // Snapshot raw PCM before changing context; original queues are untouched.
                for (int i = 0; i < count; ++i)
                {
                    newPool[i]->size = oldPool[i]->size;
                    newPool[i]->sampleRate = oldPool[i]->sampleRate;
                    newPool[i]->type = oldPool[i]->type;
                    memcpy(newPool[i]->pcm, oldPool[i]->pcm, oldPool[i]->size);
                    bool available = true;
                    for (auto queued = original->queuedHead; queued; queued = queued->nextQueued)
                        if (oldPool[i] == queued) available = false;
                    for (int j = 0; j < freeCount; ++j) if (oldPool[i] == managedFree[j]) available = false;
                    if (available) nativeFree.push_back(newPool[i]);
                }
            }
            // Rehome newly allocated IDs into this source's actual context. Managed
            // owners retain the same pool tokens; old context IDs are released first.
            for (int i = 0; i < count; ++i)
            {
                if (newPool[i]->context == replacement->listener->context) continue;
                { ContextState lock(newPool[i]->context);
                    (void)GetErrorAL();
                    DeleteBuffers(1, &newPool[i]->buffer);
                    if (GetErrorAL() != AL_NO_ERROR) return false;
                    newPool[i]->buffer = 0;
                }
                newPool[i]->context = replacement->listener->context;
                { ContextState lock(newPool[i]->context);
                    (void)GetErrorAL();
                    GenBuffers(1, &newPool[i]->buffer);
                    if (GetErrorAL() != AL_NO_ERROR || newPool[i]->buffer == 0) return false;
                }
            }
            {
                ContextState lock(replacement->listener->context);
                (void)GetErrorAL();
                for (int i = 0; i < count; ++i)
                {
                    auto buffer = newPool[i];
                    buffer->ownerListener = replacement->listener;
                    replacement->listener->buffers[buffer->buffer] = buffer;
                    if (buffer->size != 0) BufferData(buffer->buffer,
                        replacement->mono ? AL_FORMAT_MONO16 : AL_FORMAT_STEREO16,
                        buffer->pcm, buffer->size, replacement->sampleRate);
                }
                if (GetErrorAL() != AL_NO_ERROR) return false;
                for (auto buffer : pending)
                {
                    SourceQueueBuffers(replacement->source, 1, &buffer->buffer);
                    if (GetErrorAL() != AL_NO_ERROR) return false;
                    replacement->AppendQueued(buffer);
                }
                if (pending.size() != 0)
                {
                    SourceI(replacement->source, AL_SAMPLE_OFFSET, firstOffset);
                    if (GetErrorAL() != AL_NO_ERROR) return false;
                    ALint actual; GetSourceI(replacement->source, AL_SAMPLE_OFFSET, &actual);
                    if (GetErrorAL() != AL_NO_ERROR || actual != firstOffset) return false;
                }
                for (auto buffer : nativeFree) replacement->freeBuffers.push_back(buffer);
                replacement->dequeuedTime = dequeued;
            }
            return true;
        }

        DLL_EXPORT_API npBool xnAudioSourceSetPlaybackPosition(xnAudioSource* source, double seconds)
        {
            if (!source || source->streamed || !isfinite(seconds) || seconds < source->rangeStart) return false;
            ContextState lock(source->listener->context);
            (void)GetErrorAL();
            SourceF(source->source, AL_SEC_OFFSET, (ALfloat)(seconds - source->rangeStart));
            return GetErrorAL() == AL_NO_ERROR;
        }

		DLL_EXPORT_API void xnAudioSourceSetRange(xnAudioSource* source, double startTime, double stopTime)
		{
			if (source->streamed)
			{
				return;
			}

			ContextState lock(source->listener->context);

			ALint playing;
			GetSourceI(source->source, AL_SOURCE_STATE, &playing);
			if (playing == AL_PLAYING) SourceStop(source->source);
			SourceI(source->source, AL_BUFFER, 0);

            source->rangeStart = startTime;
			//OpenAL is kinda bad and offers only starting offset...
			//As result we need to rewrite the buffer
			if(startTime == 0 && stopTime == 0)
			{
				//cancel the offsetting							
				BufferData(source->singleBuffer->buffer, source->mono ? AL_FORMAT_MONO16 : AL_FORMAT_STEREO16, source->singleBuffer->pcm, source->singleBuffer->size, source->singleBuffer->sampleRate);						
			}
			else
			{
				//offset the data
				auto sampleStart = int(double(source->singleBuffer->sampleRate) * (source->mono ? 1.0 : 2.0) * startTime);
				auto sampleStop = int(double(source->singleBuffer->sampleRate) * (source->mono ? 1.0 : 2.0) * stopTime);

				if (sampleStart > source->singleBuffer->size / sizeof(short))
				{
					return; //the starting position must be less then the total length of the buffer
				}

				if (sampleStop > source->singleBuffer->size / sizeof(short)) //if the end point is more then the length of the buffer fix the value
				{
					sampleStop = source->singleBuffer->size / sizeof(short);
				}

				auto len = sampleStop - sampleStart;

				auto offsettedBuffer = source->singleBuffer->pcm + sampleStart;

				BufferData(source->singleBuffer->buffer, source->mono ? AL_FORMAT_MONO16 : AL_FORMAT_STEREO16, (void*)offsettedBuffer, len * sizeof(short), source->singleBuffer->sampleRate);
			}

			SourceI(source->source, AL_BUFFER, source->singleBuffer->buffer);
			if (playing == AL_PLAYING) SourcePlay(source->source);
		}

		DLL_EXPORT_API void xnAudioSourceSetGain(xnAudioSource* source, float gain)
		{
			ContextState lock(source->listener->context);

			SourceF(source->source, AL_GAIN, gain);
		}

		DLL_EXPORT_API void xnAudioSourceSetPitch(xnAudioSource* source, float pitch)
		{
			ContextState lock(source->listener->context);

			SourceF(source->source, AL_PITCH, pitch);
		}

		DLL_EXPORT_API void xnAudioSourceSetBuffer(xnAudioSource* source, xnAudioBuffer* buffer)
		{
			ContextState lock(source->listener->context);

			source->singleBuffer = buffer;
			SourceI(source->source, AL_BUFFER, buffer->buffer);
		}

		DLL_EXPORT_API void xnAudioSourceQueueBuffer(xnAudioSource* source, xnAudioBuffer* buffer, short* pcm, int bufferSize, BufferType type)
		{
			ContextState lock(source->listener->context);

			buffer->type = type;
			buffer->size = bufferSize;
            buffer->sampleRate = source->sampleRate;
            memcpy(buffer->pcm, pcm, bufferSize);
            buffer->ownerListener = source->listener;
			BufferData(buffer->buffer, source->mono ? AL_FORMAT_MONO16 : AL_FORMAT_STEREO16, pcm, bufferSize, source->sampleRate);
			SourceQueueBuffers(source->source, 1, &buffer->buffer);
            source->AppendQueued(buffer);
			source->listener->buffers[buffer->buffer] = buffer;
		}

		DLL_EXPORT_API xnAudioBuffer* xnAudioSourceGetFreeBuffer(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			if(source->freeBuffers.size() > 0)
			{
				auto buffer = source->freeBuffers.back();
				source->freeBuffers.pop_back();
				return buffer;
			}

			return NULL;
		}

		DLL_EXPORT_API void xnAudioSourcePlay(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			SourcePlay(source->source);
		}

		DLL_EXPORT_API void xnAudioSourcePause(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			SourcePause(source->source);
		}

		// no-lock body; caller must already hold the OpenAL context lock
        static void FlushBuffersInternal(xnAudioSource* source)
        {
            if (!source->streamed) return;
            // A playing source can retain its current packet. Never return a
            // buffer to a pool until OpenAL actually released that buffer.
            ALint processed = 0; GetSourceI(source->source, AL_BUFFERS_PROCESSED, &processed);
            while (processed-- > 0)
            {
                (void)GetErrorAL();
                ALuint id; SourceUnqueueBuffers(source->source, 1, &id);
                if (GetErrorAL() != AL_NO_ERROR) return;
                auto buffer = source->listener->buffers[id];
                source->RemoveQueued(buffer);
                source->freeBuffers.push_back(buffer);
            }
            (void)GetErrorAL();
            SourceI(source->source, AL_BUFFER, 0);
            if (GetErrorAL() != AL_NO_ERROR) return;
            // Only buffers borrowed by this source return to its native pool.
            // Managed-free buffers and other emitters' pools stay untouched.
            while (source->queuedHead)
            {
                auto buffer = source->queuedHead;
                source->RemoveQueued(buffer);
                source->freeBuffers.push_back(buffer);
            }
        }

		DLL_EXPORT_API void xnAudioSourceFlushBuffers(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			FlushBuffersInternal(source);
		}

		DLL_EXPORT_API void xnAudioSourceStop(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			SourceStop(source->source);
			FlushBuffersInternal(source);

			//reset timing info
			if(source->streamed)
				source->dequeuedTime = 0.0;
		}

		DLL_EXPORT_API void xnAudioListenerPush3D(xnAudioListener* listener, float* pos, float* forward, float* up, float* vel, Matrix* worldTransform)
		{
			ContextState lock(listener->context);

			if (forward && up)
			{
				float ori[6];
				ori[0] = forward[0];
				ori[1] = forward[1];
				ori[2] = -forward[2];
				ori[3] = up[0];
				ori[4] = up[1];
				ori[5] = -up[2];
				ListenerFV(AL_ORIENTATION, ori);
			}

			if (pos)
			{
				float pos2[3];
				pos2[0] = pos[0];
				pos2[1] = pos[1];
				pos2[2] = -pos[2];
				ListenerFV(AL_POSITION, pos2);
			}

			if (vel)
			{
				float vel2[3];
				vel2[0] = vel[0];
				vel2[1] = vel[1];
				vel2[2] = -vel[2];
				ListenerFV(AL_VELOCITY, vel2);
			}
		}

		DLL_EXPORT_API void xnAudioSourcePush3D(xnAudioSource* source, float* pos, float* forward, float* up, float* vel, Matrix* worldTransform)
		{
			ContextState lock(source->listener->context);

			if (forward && up)
			{
				float ori[6];
				ori[0] = forward[0];
				ori[1] = forward[1];
				ori[2] = -forward[2];
				ori[3] = up[0];
				ori[4] = up[1];
				ori[5] = -up[2];
				SourceFV(source->source, AL_ORIENTATION, ori);
			}

			if (pos)
			{
				float pos2[3];
				pos2[0] = pos[0];
				pos2[1] = pos[1];
				pos2[2] = -pos[2];
				SourceFV(source->source, AL_POSITION, pos2);
			}

			if (vel)
			{
				float vel2[3];
				vel2[0] = vel[0];
				vel2[1] = vel[1];
				vel2[2] = -vel[2];
				SourceFV(source->source, AL_VELOCITY, vel2);
			}
		}

		DLL_EXPORT_API npBool xnAudioSourceIsPlaying(xnAudioSource* source)
		{
			ContextState lock(source->listener->context);

			ALint value;
			GetSourceI(source->source, AL_SOURCE_STATE, &value);
			return value == AL_PLAYING || value == AL_PAUSED;
		}

		DLL_EXPORT_API xnAudioBuffer* xnAudioBufferCreate(int maxBufferSize)
		{
			auto res = new xnAudioBuffer;
			res->pcm = (short*)malloc(maxBufferSize);
            if (!res->pcm) { delete res; return NULL; }
            res->capacityBytes = maxBufferSize;
            res->context = GetCurrentContext();
            if (!res->context) { free(res->pcm); delete res; return NULL; }
            ContextState lock(res->context);
            (void)GetErrorAL();
			GenBuffers(1, &res->buffer);
            if (GetErrorAL() != AL_NO_ERROR) { free(res->pcm); delete res; return NULL; }
			return res;
		}

		DLL_EXPORT_API void xnAudioBufferDestroy(xnAudioBuffer* buffer)
		{
			ContextState lock(buffer->context);
            if (buffer->ownerListener) buffer->ownerListener->buffers.erase(buffer->buffer);
            if (buffer->buffer != 0) DeleteBuffers(1, &buffer->buffer);
			free(buffer->pcm);
			delete buffer;
		}

		DLL_EXPORT_API void xnAudioBufferFill(xnAudioBuffer* buffer, short* pcm, int bufferSize, int sampleRate, npBool mono)
		{
			ContextState lock(buffer->context);
            //we have to keep a copy sadly because we might need to offset the data at some point			
			memcpy(buffer->pcm, pcm, bufferSize);
			buffer->size = bufferSize;
			buffer->sampleRate = sampleRate;
			
			BufferData(buffer->buffer, mono ? AL_FORMAT_MONO16 : AL_FORMAT_STEREO16, pcm, bufferSize, sampleRate);
		}
		
	}
}

#endif
